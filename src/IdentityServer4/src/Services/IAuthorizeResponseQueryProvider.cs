using System.Collections.Specialized;
using System.Threading.Tasks;
using IdentityServer4.ResponseHandling;

namespace IdentityServer4.Services
{
    /// <summary>
    /// Extensibility point for services that augment the query/hash parameters of an <see cref="AuthorizeResponse" />
    /// </summary>
    public interface IAuthorizeResponseQueryProvider
    {
        /// <summary>
        /// Called to add query parameters to the redirect_uri
        /// </summary>
        /// <param name="response"></param>
        /// <param name="query"></param>
        /// <returns></returns>
        Task AugmentRedirectUriAsync(AuthorizeResponse response, NameValueCollection query);
    }
}
