using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;

namespace AspNetCoreVerifiableCredentials.Pages
{
    public class VerifierModel : PageModel
    {
        private IConfiguration _configuration;
        public VerifierModel( IConfiguration configuration ) {
            _configuration = configuration;
        }
        protected string GetRequestHostName() {
            string scheme = this.Request.Scheme;
            string originalHost = this.Request.Headers["x-original-host"];
            string hostname = "";
            if (!string.IsNullOrEmpty( originalHost ))
                hostname = string.Format( "{0}://{1}", scheme, originalHost );
            else hostname = string.Format( "{0}://{1}", scheme, this.Request.Host );
            return hostname;
        }

        public void OnGet()
        {
            string idvUrl = _configuration.GetSection( "AppSettings" )["IdvUrl"];
            string idvQueryStringParams = _configuration.GetSection( "AppSettings" )["IdvQueryStringParams"];
            //string returnUrl = HttpUtility.UrlEncode( $"{GetRequestHostName()}?returnFromIdv=1" );
            string returnUrl = HttpUtility.UrlEncode( $"{this.HttpContext.Request.GetDisplayUrl()}" );
            string link = $"{idvUrl}?returnUrl={returnUrl}&{idvQueryStringParams}";
            ViewData["idvLink"] = link;
        }
    }
}
