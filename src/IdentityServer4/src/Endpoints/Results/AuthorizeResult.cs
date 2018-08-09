// Copyright (c) Brock Allen & Dominick Baier. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.


using System.Threading.Tasks;
using IdentityServer4.Models;
using IdentityServer4.Extensions;
using IdentityServer4.Hosting;
using IdentityModel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System;
using IdentityServer4.Services;
using IdentityServer4.Configuration;
using IdentityServer4.Stores;
using IdentityServer4.ResponseHandling;
using Microsoft.AspNetCore.Authentication;
using System.Text.Encodings.Web;
using System.Collections.Generic;
using System.Collections.Specialized;

namespace IdentityServer4.Endpoints.Results
{
    internal class AuthorizeResult : IEndpointResult
    {
        public AuthorizeResponse Response { get; }

        public AuthorizeResult(AuthorizeResponse response, IEnumerable<IAuthorizeResponseQueryProvider> queryProviders)
        {
            Response = response ?? throw new ArgumentNullException(nameof(response));
            _queryProviders = queryProviders;
        }

        internal AuthorizeResult(
            AuthorizeResponse response,
            IdentityServerOptions options,
            IUserSession userSession,
            IMessageStore<ErrorMessage> errorMessageStore,
            IEnumerable<IAuthorizeResponseQueryProvider> queryProviders,
            ISystemClock clock)
            : this(response, queryProviders)
        {
            _options = options;
            _userSession = userSession;
            _errorMessageStore = errorMessageStore;
            _clock = clock;
        }

        private IdentityServerOptions _options;
        private IUserSession _userSession;
        private IMessageStore<ErrorMessage> _errorMessageStore;
        private readonly IEnumerable<IAuthorizeResponseQueryProvider> _queryProviders;
        private ISystemClock _clock;

        private void Init(HttpContext context)
        {
            _options = _options ?? context.RequestServices.GetRequiredService<IdentityServerOptions>();
            _userSession = _userSession ?? context.RequestServices.GetRequiredService<IUserSession>();
            _errorMessageStore = _errorMessageStore ?? context.RequestServices.GetRequiredService<IMessageStore<ErrorMessage>>();
            _clock = _clock ?? context.RequestServices.GetRequiredService<ISystemClock>();
        }

        public async Task ExecuteAsync(HttpContext context)
        {
            Init(context);

            if (Response.IsError)
            {
                await ProcessErrorAsync(context);
            }
            else
            {
                await ProcessResponseAsync(context);
            }
        }

        private async Task ProcessErrorAsync(HttpContext context)
        {
            // these are the conditions where we can send a response 
            // back directly to the client, otherwise we're only showing the error UI
            var isPromptNoneError = Response.Error == OidcConstants.AuthorizeErrors.AccountSelectionRequired ||
                Response.Error == OidcConstants.AuthorizeErrors.LoginRequired ||
                Response.Error == OidcConstants.AuthorizeErrors.ConsentRequired ||
                Response.Error == OidcConstants.AuthorizeErrors.InteractionRequired;

            if (Response.Error == OidcConstants.AuthorizeErrors.AccessDenied ||
                (isPromptNoneError && Response.Request?.PromptMode == OidcConstants.PromptModes.None)
            )
            {
                // this scenario we can return back to the client
                await ProcessResponseAsync(context);
            }
            else
            {
                // we now know we must show error page
                await RedirectToErrorPageAsync(context);
            }
        }

        protected async Task ProcessResponseAsync(HttpContext context)
        {
            if (!Response.IsError)
            {
                // success response -- track client authorization for sign-out
                //_logger.LogDebug("Adding client {0} to client list cookie for subject {1}", request.ClientId, request.Subject.GetSubjectId());
                await _userSession.AddClientIdAsync(Response.Request.ClientId);
            }

            await RenderAuthorizeResponseAsync(context);
        }

        protected async Task RenderAuthorizeResponseAsync(HttpContext context)
        {
            if (Response.Request.ResponseMode == OidcConstants.ResponseModes.Query ||
                Response.Request.ResponseMode == OidcConstants.ResponseModes.Fragment)
            {
                context.Response.SetNoCache();
                context.Response.Redirect(await BuildRedirectUriAsync());
            }
            else if (Response.Request.ResponseMode == OidcConstants.ResponseModes.FormPost)
            {
                context.Response.SetNoCache();
                AddSecurityHeaders(context);
                await context.Response.WriteHtmlAsync(GetFormPostHtml());
            }
            else
            {
                //_logger.LogError("Unsupported response mode.");
                throw new InvalidOperationException("Unsupported response mode");
            }
        }

        private void AddSecurityHeaders(HttpContext context)
        {
            context.Response.AddScriptCspHeaders(_options.Csp, "sha256-orD0/VhH8hLqrLxKHD/HUEMdwqX6/0ve7c5hspX5VJ8=");

            var referrer_policy = "no-referrer";
            if (!context.Response.Headers.ContainsKey("Referrer-Policy"))
            {
                context.Response.Headers.Add("Referrer-Policy", referrer_policy);
            }
        }

        private async Task<string> BuildRedirectUriAsync()
        {
            var uri = Response.RedirectUri;
            var query = Response.ToNameValueCollection();

            await AugmentRedirectUriAsync(query);

            var queryString = query.ToQueryString();
            if (Response.Request.ResponseMode == OidcConstants.ResponseModes.Query)
            {
                uri = uri.AddQueryString(queryString);
            }
            else
            {
                uri = uri.AddHashFragment(queryString);
            }

            if (Response.IsError && !uri.Contains("#"))
            {
                // https://tools.ietf.org/html/draft-bradley-oauth-open-redirector-00
                uri += "#_=_";
            }

            return uri;
        }

        private async Task AugmentRedirectUriAsync(NameValueCollection query)
        {
            foreach(var provider in _queryProviders)
            {
                await provider.AugmentRedirectUriAsync(Response, query);
            }
        }

        private const string FormPostHtml = "<html><head><base target='_self'/></head><body><form method='post' action='{uri}'>{body}<noscript><button>Click to continue</button></noscript></form><script>(function(){document.forms[0].submit();})();</script></body></html>";

        private string GetFormPostHtml()
        {
            var html = FormPostHtml;

            var url = Response.Request.RedirectUri;
            url = HtmlEncoder.Default.Encode(url);
            html = html.Replace("{uri}", url);
            html = html.Replace("{body}", Response.ToNameValueCollection().ToFormPost());

            return html;
        }

        private async Task RedirectToErrorPageAsync(HttpContext context)
        {
            var errorModel = new ErrorMessage
            {
                RequestId = context.TraceIdentifier,
                Error = Response.Error,
                ErrorDescription = Response.ErrorDescription,
                UiLocales = Response.Request?.UiLocales,
                DisplayMode = Response.Request?.DisplayMode,
                ClientId = Response.Request?.ClientId
            };

            if (Response.RedirectUri != null && Response.Request?.ResponseMode != null)
            {
                // if we have a valid redirect uri, then include it to the error page
                errorModel.RedirectUri = await BuildRedirectUriAsync();
                errorModel.ResponseMode = Response.Request.ResponseMode;
            }

            var message = new Message<ErrorMessage>(errorModel, _clock.UtcNow.UtcDateTime);
            var id = await _errorMessageStore.WriteAsync(message);

            var errorUrl = _options.UserInteraction.ErrorUrl;

            var url = errorUrl.AddQueryString(_options.UserInteraction.ErrorIdParameter, id);
            context.Response.RedirectToAbsoluteUrl(url);
        }
    }
}
