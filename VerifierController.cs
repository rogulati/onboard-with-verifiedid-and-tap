﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Identity.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography.X509Certificates;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Azure.Identity;
using Microsoft.Identity.Client.Cache;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace AspNetCoreVerifiableCredentials
{
    [Route("api/[controller]/[action]")]
    public class VerifierController : Controller
    {
        // const string PRESENTATIONPAYLOAD = "presentation_request_config.json";
        const string PRESENTATIONPAYLOAD = "presentation_request_config - TrueIdentitySample.json";

        protected readonly AppSettingsModel AppSettings;
        protected IMemoryCache _cache;
        protected readonly ILogger<VerifierController> _log;



        public VerifierController(IOptions<AppSettingsModel> appSettings, IMemoryCache memoryCache, ILogger<VerifierController> log)
        {
            this.AppSettings = appSettings.Value;
            _cache = memoryCache;
            _log = log;
        }

        /// <summary>
        /// This method is called from the UI to initiate the presentation of the verifiable credential
        /// </summary>
        /// <returns>JSON object with the address to the presentation request and optionally a QR code and a state value which can be used to check on the response status</returns>
        [HttpGet("/api/verifier/presentation-request")]
        public async Task<ActionResult> PresentationRequest()
        {
            try
            {

                string jsonString = null;
                //they payload template is loaded from disk and modified in the code below to make it easier to get started
                //and having all config in a central location appsettings.json. 
                //if you want to manually change the payload in the json file make sure you comment out the code below which will modify it automatically
                //
                string payloadpath = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location), PRESENTATIONPAYLOAD);
                _log.LogTrace("IssuanceRequest file: {0}", payloadpath);
                if (!System.IO.File.Exists(payloadpath))
                {
                    _log.LogError("File not found: {0}", payloadpath);
                    return BadRequest(new { error = "400", error_description = PRESENTATIONPAYLOAD + " not found" });
                }
                jsonString = System.IO.File.ReadAllText(payloadpath);
                if (string.IsNullOrEmpty(jsonString))
                {
                    _log.LogError("Error reading file: {0}", payloadpath);
                    return BadRequest(new { error = "400", error_description = PRESENTATIONPAYLOAD + " error reading file" });
                }

                string state = Guid.NewGuid().ToString();

                //modify payload with new state, the state is used to be able to update the UI when callbacks are received from the VC Service
                JObject payload = JObject.Parse(jsonString);
                if (payload["callback"]["state"] != null)
                {
                    payload["callback"]["state"] = state;
                }

                //get the VerifierDID from the appsettings
                if (payload["authority"] != null)
                {
                    payload["authority"] = AppSettings.VerifierAuthority;
                }

                // TBD - This needs to be removed and README updated so developer does not need to update appsettings.json 
                // considering the payload already has the accepted issuers. 
                //copy the issuerDID from the settings and fill in the trustedIssuer part of the payload
                //this means only that issuer should be trusted for the requested credentialtype
                //this value is an array in the payload, you can trust multiple issuers for the same credentialtype
                //very common to accept the test VCs and the Production VCs coming from different verifiable credential services
                if (payload["requestedCredentials"][0]["acceptedIssuers"] != null &&
                    payload["requestedCredentials"][0]["acceptedIssuers"][0] != null)
                {
                    payload["requestedCredentials"][0]["acceptedIssuers"][0] = AppSettings.IssuerAuthority;
                }

                //modify the callback method to make it easier to debug with tools like ngrok since the URI changes all the time
                //this way you don't need to modify the callback URL in the payload every time ngrok changes the URI
                if (payload["callback"]["url"] != null)
                {
                    //localhost hostname can't work for callbacks so we won't overwrite it.
                    //this happens for example when testing with sign-in to an IDP and https://localhost is used as redirect URI
                    //in that case the callback should be configured in the payload directly instead of being modified in the code here
                    string host = GetRequestHostName();
                    if (!host.Contains("//localhost"))
                    {
                        payload["callback"]["url"] = String.Format("{0}:/api/verifier/presentationCallback", host);
                    }
                }

                jsonString = JsonConvert.SerializeObject(payload);

                _log.LogTrace( jsonString );

                //CALL REST API WITH PAYLOAD
                HttpStatusCode statusCode = HttpStatusCode.OK;
                string response = null;
                try
                {
                    //The VC Request API is an authenticated API. We need to clientid and secret (or certificate) to create an access token which 
                    //needs to be send as bearer to the VC Request API
                    var accessToken = await GetAccessTokenForVCService();
                    if (accessToken.Item1 == String.Empty)
                    {
                        _log.LogError(String.Format("failed to acquire accesstoken: {0} : {1}"), accessToken.error, accessToken.error_description);
                        return BadRequest(new { error = accessToken.error, error_description = accessToken.error_description });
                    }

                    HttpClient client = new HttpClient();
                    var defaultRequestHeaders = client.DefaultRequestHeaders;
                    defaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.token);

                    HttpResponseMessage res = await client.PostAsync(AppSettings.ApiEndpoint +
                        "verifiableCredentials/createPresentationRequest", new StringContent(jsonString, Encoding.UTF8, "application/json"));
                    response = await res.Content.ReadAsStringAsync();
                    _log.LogTrace("succesfully called Request API");
                    client.Dispose();
                    statusCode = res.StatusCode;

                    if (statusCode == HttpStatusCode.Created)
                    {
                        JObject requestConfig = JObject.Parse(response);
                        requestConfig.Add(new JProperty("id", state));
                        jsonString = JsonConvert.SerializeObject(requestConfig);

                        //We use in memory cache to keep state about the request. The UI will check the state when calling the presentationResponse method

                        var cacheData = new
                        {
                            status = "notscanned",
                            message = "Request ready, please scan with Authenticator",
                            expiry = requestConfig["expiry"].ToString()
                        };
                        _cache.Set(state, JsonConvert.SerializeObject(cacheData));

                        //the response from the VC Request API call is returned to the caller (the UI). It contains the URI to the request which Authenticator can download after
                        //it has scanned the QR code. If the payload requested the VC Request service to create the QR code that is returned as well
                        //the javascript in the UI will use that QR code to display it on the screen to the user.

                        return new ContentResult { ContentType = "application/json", Content = jsonString };
                    }
                    else
                    {
                        _log.LogError("Unsuccesfully called Request API" + response);
                        return BadRequest(new { error = "400", error_description = "Something went wrong calling the API: " + response });
                    }
                }
                catch (Exception ex)
                {
                    return BadRequest(new { error = "400", error_description = "Something went wrong calling the API: " + ex.Message });
                }
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = "400", error_description = ex.Message });
            }
        }

        [HttpGet("/api/createtap/{id}")]
        public async Task<ActionResult> CreateTap( string id) {
            try {
                //the id is the state value initially created when the issuanc request was requested from the request API
                //the in-memory database uses this as key to get and store the state of the process so the UI can be updated
                /**/
                if (string.IsNullOrEmpty( id )) {
                    _log.LogTrace( $"Missing argument 'id'" );
                    return BadRequest( new { error = "400", error_description = "Missing argument 'id'" } );
                }
                if (!_cache.TryGetValue( id, out string buf )) {
                    _log.LogTrace( $"Cached data not found for id: {id}" );
                    return new NotFoundResult();
                }
                JObject cachedData = JObject.Parse( buf );
                if (!cachedData.ContainsKey("status") || cachedData["status"].ToString() != "presentation_verified") {
                    return BadRequest( new { error = "400", error_description = $"Wrong status in cached data" } );
                }
                if (!cachedData.ContainsKey( "firstName" ) || !cachedData.ContainsKey( "lastName" ) ) {
                    return BadRequest( new { error = "400", error_description = $"firstName/lastName missing in cached data" } );
                }

                string firstName = cachedData["firstName"].ToString();
                string lastName = cachedData["lastName"].ToString();
                /**/
                /*
                string firstName = "Jade";
                string lastName = "Dsouza";
                */
                var mgClient = GetGraphClient();
                var userFound = await mgClient.Users
                    .Request()
                    .Filter( $"givenName eq '{firstName}' and surname eq '{lastName}'" )
                    .GetAsync();

                if (userFound == null || (null != userFound && userFound.Count == 0) ) {
                    return BadRequest( new { error = "400", error_description = $"User not found" } );
                }
                if (userFound == null || (null != userFound && userFound.Count > 1)) {
                    return BadRequest( new { error = "400", error_description = $"Multiple users found" } );
                }
                var userObjectId = userFound[0].Id;
                var userUPN = userFound[0].UserPrincipalName;

                //Cherry on top, get the user's photo, any other information 

                //TODO: code below will fail if the user doesnt have TAP as an allowed Auth Method.Need to handle that case.
                //NOTE: Right now above is listed as a setup requirement in the README file. 

                // delete any old TAPs so we can create a new one
                var existingTap = mgClient.Users[userObjectId].Authentication.TemporaryAccessPassMethods.Request().GetAsync();
                if (existingTap != null && existingTap.Result != null && existingTap.Result.Count > 1 ) {
                    foreach( var eTap in existingTap.Result ) {
                        await mgClient.Users[userObjectId].Authentication.TemporaryAccessPassMethods[eTap.Id].Request().DeleteAsync();
                    }
                }
                // now create a new TAP code
                TemporaryAccessPassAuthenticationMethod tap = new TemporaryAccessPassAuthenticationMethod();
                var tapResult = await mgClient.Users[userObjectId].Authentication.TemporaryAccessPassMethods.Request().AddAsync( tap );
                tap = tapResult;

                var cacheData = new {
                    status = (null != tap.TemporaryAccessPass ? "tap_created" : "tap_exists"),
                    message = $"Welcome aboard, {firstName}.",
                    userFirstName = firstName,
                    userLastName = lastName,
                    userUPN = userUPN,
                    userObjectId = userObjectId,
                    tap = tap.TemporaryAccessPass,
                    expiresUtc = DateTime.UtcNow.AddMinutes( (double)tap.LifetimeInMinutes ),
                    payload = $"userUPN={userUPN}, objectId={userObjectId}, tap={tap.TemporaryAccessPass}"
                };
                _log.LogTrace( $"{cacheData.message}.objectId={userObjectId}, UPN={userUPN}" );
                _cache.Set( id, JsonConvert.SerializeObject( cacheData ) );
                //return new OkResult();
                return new ContentResult { ContentType = "application/json", Content = JsonConvert.SerializeObject( cacheData ) };
            } catch (Exception ex) {
                return BadRequest( new { error = "400", error_description = ex.Message } );
            }
        }
        /// <summary>
        /// This method is called by the VC Request API when the user scans a QR code and presents a Verifiable Credential to the service
        /// </summary>
        /// <returns></returns>
        [HttpPost( "/api/verifier/presentationCallback" )]
        public async Task<ActionResult> PresentationCallback()
        {
            try
            {
                string content = await new System.IO.StreamReader(this.Request.Body).ReadToEndAsync();
                Debug.WriteLine("callback!: " + content);
                JObject presentationResponse = JObject.Parse(content);
                var state = presentationResponse["state"].ToString();

                //there are 2 different callbacks. 1 if the QR code is scanned (or deeplink has been followed)
                //Scanning the QR code makes Authenticator download the specific request from the server
                //the request will be deleted from the server immediately.
                //That's why it is so important to capture this callback and relay this to the UI so the UI can hide
                //the QR code to prevent the user from scanning it twice (resulting in an error since the request is already deleted)
                if (presentationResponse["requestStatus"].ToString() == "request_retrieved")
                {
                    var cacheData = new
                    {
                        status = "request_retrieved",
                        message = "QR Code is scanned. Waiting for validation...",
                    };
                    _cache.Set(state, JsonConvert.SerializeObject(cacheData));
                }

                // the 2nd callback is the result with the verified credential being verified.
                // typically here is where the business logic is written to determine what to do with the result
                // the response in this callback contains the claims from the Verifiable Credential(s) being presented by the user
                // In this case the result is put in the in memory cache which is used by the UI when polling for the state so the UI can be updated.
                if (presentationResponse["requestStatus"].ToString() == "presentation_verified")
                {
                    string firstName = presentationResponse["verifiedCredentialsData"][0]["claims"]["firstName"].ToString();
                    string lastName = presentationResponse["verifiedCredentialsData"][0]["claims"]["lastName"].ToString();
                    var cacheData = new {
                        status = "presentation_verified",
                        message = $"Presentation verified of {firstName} {lastName} from IDV",
                        firstName = firstName,
                        lastName = lastName
                    };
                    _log.LogTrace( $"{cacheData.status}, firstName={firstName}, lastName={lastName}" );
                    _cache.Set( state, JsonConvert.SerializeObject( cacheData ) );
                }

                return new OkResult();
            }
            catch (Exception ex)
            {
                _log.LogTrace( ex.Message );
                return BadRequest(new { error = "400", error_description = ex.Message });
            }
        }
        //
        //this function is called from the UI polling for a response from the AAD VC Service.
        //when a callback is recieved at the presentationCallback service the session will be updated
        //this method will respond with the status so the UI can reflect if the QR code was scanned and with the result of the presentation
        //
        [HttpGet("/api/verifier/presentation-response")]
        public ActionResult PresentationResponse()
        {

            try
            {
                //the id is the state value initially created when the issuanc request was requested from the request API
                //the in-memory database uses this as key to get and store the state of the process so the UI can be updated
                string state = this.Request.Query["id"];
                if (string.IsNullOrEmpty(state))
                {
                    return BadRequest(new { error = "400", error_description = "Missing argument 'id'" });
                }
                JObject value = null;
                if (_cache.TryGetValue(state, out string buf))
                {
                    value = JObject.Parse(buf);

                    Debug.WriteLine("check if there was a response yet: " + value);
                    return new ContentResult { ContentType = "application/json", Content = JsonConvert.SerializeObject(value) };
                }

                return new OkResult();
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = "400", error_description = ex.Message });
            }


        }

        [HttpGet("/api/verifier/account-set-up")]
        public async Task<ActionResult> AccountSetupDone()
        {
            try

            {
                //retrieve the right cache from state

                string state = this.Request.Query["id"];
                if (string.IsNullOrEmpty(state))
                {
                    return BadRequest(new { error = "400", error_description = "Missing argument 'id'" });
                }
                JObject value = null;
                if (_cache.TryGetValue(state, out string buf))
                {
                    value = JObject.Parse(buf);
                    string targetUserObjectId = value["userObjectId"].ToString(); //TODO: Figure this out

                    var mgClient = GetGraphClient();

                    var authenticatorAppResult = await mgClient.Users[targetUserObjectId].Authentication.MicrosoftAuthenticatorMethods
                                .Request()
                                .GetAsync();

                    //TODO: after authenticator app is added to the user, you can consider manipulating group membership to 
                    //'unblock' the account from 


                    if (authenticatorAppResult != null && authenticatorAppResult.Count > 0)
                    {
                        var cacheData = new
                        {
                            deviceDisplayName = authenticatorAppResult[0].DisplayName,
                            deviceTag = authenticatorAppResult[0].DeviceTag,
                            phoneAppVersion = authenticatorAppResult[0].PhoneAppVersion,
                            status = "account_setup_done",
                            message = $"Authenticator App Phone Sign In configured in Device: {authenticatorAppResult[0].DisplayName} - Platform: {authenticatorAppResult[0].DeviceTag}"
                        };
                        _log.LogTrace( cacheData.message );
                        return new ContentResult { ContentType = "application/json", Content = JsonConvert.SerializeObject(cacheData) };
                    }
                    else
                    {
                        var cacheData = new
                        {
                            status = "account_setup_in_progress",
                            message = "Waiting for account set up"
                        };
                        _log.LogTrace( cacheData.message );
                        return new ContentResult { ContentType = "application/json", Content = JsonConvert.SerializeObject(cacheData) };
                    }
                }
                return new OkResult();
            }
            catch (Exception ex)
            {
                _log.LogTrace( ex.Message );
                return BadRequest(new { error = "400", error_description = ex.Message });
            }
        }

        //some helper functions

        protected async Task<(string token, string error, string error_description)> GetAccessTokenForVCService()
        {
            var coreTask = await GetAccessTokenCommon(AppSettings.VCServiceScope);
            return coreTask;
        }

        protected GraphServiceClient GetGraphClient()
        {
            // The client credentials flow requires that you request the
            // /.default scope, and preconfigure your permissions on the
            // app registration in Azure. An administrator must grant consent
            // to those permissions beforehand.
            var scopes = new[] { "https://graph.microsoft.com/.default" };

            // Multi-tenant apps can use "common",
            // single-tenant apps must use the tenant ID from the Azure portal
            var tenantId = AppSettings.TenantId;

            // Values from app registration
            var clientId = AppSettings.ClientId;
            var clientSecret = AppSettings.ClientSecret;

            // using Azure.Identity;
            var options = new TokenCredentialOptions
            {
                AuthorityHost = AzureAuthorityHosts.AzurePublicCloud
            };

            var clientSecretCredential = new ClientSecretCredential(
                tenantId, clientId, clientSecret, options);

            return new GraphServiceClient(clientSecretCredential, scopes);
        }

        protected async Task<(string token, string error, string error_description)> GetAccessTokenCommon(string tokenScopes)
        {
            // You can run this sample using ClientSecret or Certificate. The code will differ only when instantiating the IConfidentialClientApplication
            bool isUsingClientSecret = AppSettings.AppUsesClientSecret(AppSettings);

            // Since we are using application permissions this will be a confidential client application
            IConfidentialClientApplication app;
            if (isUsingClientSecret)
            {
                app = ConfidentialClientApplicationBuilder.Create(AppSettings.ClientId)
                    .WithClientSecret(AppSettings.ClientSecret)
                    .WithAuthority(new Uri(AppSettings.Authority))
                    .Build();
            }
            else
            {
                X509Certificate2 certificate = AppSettings.ReadCertificate(AppSettings.CertificateName);
                app = ConfidentialClientApplicationBuilder.Create(AppSettings.ClientId)
                    .WithCertificate(certificate)
                    .WithAuthority(new Uri(AppSettings.Authority))
                    .Build();
            }

            //configure in memory cache for the access tokens. The tokens are typically valid for 60 seconds,
            //so no need to create new ones for every web request
            app.AddDistributedTokenCache(services =>
            {
                services.AddDistributedMemoryCache();
                services.AddLogging(configure => configure.AddConsole())
                .Configure<LoggerFilterOptions>(options => options.MinLevel = Microsoft.Extensions.Logging.LogLevel.Debug);
            });

            // With client credentials flows the scopes is ALWAYS of the shape "resource/.default", as the 
            // application permissions need to be set statically (in the portal or by PowerShell), and then granted by
            // a tenant administrator. 
            string[] scopes = new string[] { tokenScopes };

            AuthenticationResult result = null;
            try
            {
                result = await app.AcquireTokenForClient(scopes)
                    .ExecuteAsync();
            }
            catch (MsalServiceException ex) when (ex.Message.Contains("AADSTS70011"))
            {
                // Invalid scope. The scope has to be of the form "https://resourceurl/.default"
                // Mitigation: change the scope to be as expected
                return (string.Empty, "500", "Scope provided is not supported");
                //return BadRequest(new { error = "500", error_description = "Scope provided is not supported" });
            }
            catch (MsalServiceException ex)
            {
                // general error getting an access token
                return (String.Empty, "500", "Something went wrong getting an access token for the client API:" + ex.Message);
                //return BadRequest(new { error = "500", error_description = "Something went wrong getting an access token for the client API:" + ex.Message });
            }

            _log.LogTrace(result.AccessToken);
            return (result.AccessToken, String.Empty, String.Empty);
        }
        protected string GetRequestHostName()
        {
            string scheme = "https";// : this.Request.Scheme;
            string originalHost = this.Request.Headers["x-original-host"];
            string hostname = "";
            if (!string.IsNullOrEmpty(originalHost))
                hostname = string.Format("{0}://{1}", scheme, originalHost);
            else hostname = string.Format("{0}://{1}", scheme, this.Request.Host);
            return hostname;
        }
    }
}
