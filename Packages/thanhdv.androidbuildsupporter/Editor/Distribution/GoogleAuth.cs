using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ThanhDV.AndroidBuildSupporter
{
    /// <summary>
    /// OAuth for a Google "Desktop app" client: the browser signs in and returns to a loopback address, with PKCE.
    /// With builds in My Drive it asks only for drive.file: the tool sees nothing but the folders and files it
    /// created. A folder someone else shared (Root folder) needs the full drive scope, asked for only then.
    /// </summary>
    internal static class GoogleAuth
    {
        private const string AuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth";
        private const string TokenUrl = "https://oauth2.googleapis.com/token";
        private const string RevokeUrl = "https://oauth2.googleapis.com/revoke";
        private const string FileScope = "https://www.googleapis.com/auth/drive.file";
        private const string DriveScope = "https://www.googleapis.com/auth/drive";
        private const int SignInTimeoutSeconds = 300;

        [Serializable]
        private sealed class TokenResponse
        {
            public string access_token;
            public int expires_in;
            public string refresh_token;
            public string scope;
            public string error;
            public string error_description;
        }

        private static string accessToken;
        private static DateTime expiresAt;

        public static bool IsSignedIn => !string.IsNullOrEmpty(LocalSettings.instance.googleRefreshToken);
        public static bool IsSigningIn { get; private set; }

        /// <summary>The scope the current Drive settings need.</summary>
        private static string RequiredScope => GoogleDrive.UsesSharedFolder ? DriveScope : FileScope;

        /// <summary>
        /// Whether the saved sign-in covers the current settings: signed in for My Drive, then a shared Root folder
        /// set, means signing in again. Sign-ins saved before scopes were recorded count as drive.file.
        /// </summary>
        public static bool HasRequiredScope
        {
            get
            {
                if (!IsSignedIn) return false;
                string granted = LocalSettings.instance.googleScope;
                bool full = !string.IsNullOrEmpty(granted) && Array.IndexOf(granted.Split(' '), DriveScope) >= 0;
                return full || RequiredScope == FileScope;
            }
        }

        public static async Task SignInAsync()
        {
            LocalSettings local = LocalSettings.instance;
            if (string.IsNullOrWhiteSpace(local.googleClientId) || string.IsNullOrWhiteSpace(local.googleClientSecret))
                throw new InvalidOperationException("Enter the Client ID and Client secret of your Google OAuth client first.");
            if (IsSigningIn) return;

            IsSigningIn = true;
            string redirect = $"http://127.0.0.1:{FreePort()}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(redirect);
            try
            {
                listener.Start();
                string verifier = RandomString(64);
                string state = RandomString(24);
                Application.OpenURL(AuthorizeUrl +
                                    "?client_id=" + Uri.EscapeDataString(local.googleClientId.Trim()) +
                                    "&redirect_uri=" + Uri.EscapeDataString(redirect) +
                                    "&response_type=code" +
                                    "&scope=" + Uri.EscapeDataString(RequiredScope) +
                                    "&code_challenge=" + Base64Url(SHA256.Create().ComputeHash(Encoding.ASCII.GetBytes(verifier))) +
                                    "&code_challenge_method=S256" +
                                    "&access_type=offline&prompt=consent" +
                                    "&state=" + state);

                Task<HttpListenerContext> request = listener.GetContextAsync();
                if (await Task.WhenAny(request, Task.Delay(SignInTimeoutSeconds * 1000)) != request)
                    throw new TimeoutException("Google sign-in timed out.");

                HttpListenerContext context = request.Result;
                string code = context.Request.QueryString["code"];
                string error = context.Request.QueryString["error"];
                bool valid = context.Request.QueryString["state"] == state && !string.IsNullOrEmpty(code);
                Respond(context, valid ? "Signed in. You can close this tab and go back to Unity." : "Sign-in failed. Go back to Unity and try again.");
                if (!valid) throw new InvalidOperationException("Google sign-in failed: " + (error ?? "the answer did not match the request"));

                TokenResponse token = await RequestToken(new Dictionary<string, string>
                {
                    { "code", code },
                    { "client_id", local.googleClientId.Trim() },
                    { "client_secret", local.googleClientSecret.Trim() },
                    { "redirect_uri", redirect },
                    { "grant_type", "authorization_code" },
                    { "code_verifier", verifier }
                });
                if (string.IsNullOrEmpty(token.refresh_token))
                    throw new InvalidOperationException("Google returned no refresh token. Remove the app's access in your Google account and sign in again.");

                local.googleRefreshToken = token.refresh_token;
                local.googleScope = string.IsNullOrEmpty(token.scope) ? RequiredScope : token.scope;
                local.googleAccount = "";
                local.Save();
                Remember(token);
            }
            finally
            {
                IsSigningIn = false;
                listener.Close();
            }
            await LoadAccountAsync();
        }

        /// <summary>
        /// Reads the signed-in account's name and email from Drive's about API (allowed by drive.file) and saves
        /// them for the window. Failing only leaves the account unnamed.
        /// </summary>
        public static async Task LoadAccountAsync()
        {
            if (!IsSignedIn) return;
            try
            {
                string account = await GoogleDrive.AccountAsync();
                LocalSettings local = LocalSettings.instance;
                if (!IsSignedIn || account == local.googleAccount) return;
                local.googleAccount = account;
                local.Save();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[Android Build Supporter] Could not read the Google account name: " + e.Message);
            }
        }

        public static async Task SignOutAsync()
        {
            LocalSettings local = LocalSettings.instance;
            string refresh = local.googleRefreshToken;
            local.googleRefreshToken = "";
            local.googleScope = "";
            local.googleAccount = "";
            local.Save();
            accessToken = null;
            GoogleDrive.ClearCache();
            if (string.IsNullOrEmpty(refresh)) return;

            using (UnityWebRequest revoke = UnityWebRequest.Post(RevokeUrl, new Dictionary<string, string> { { "token", refresh } }))
            {
                try
                {
                    await Http.SendAsync(revoke);
                }
                catch (HttpException)
                {
                    // Already revoked or expired; the local token is gone either way.
                }
            }
        }

        /// <summary>A valid access token, refreshed when it is about to expire.</summary>
        public static async Task<string> GetAccessTokenAsync()
        {
            if (accessToken != null && DateTime.UtcNow < expiresAt) return accessToken;

            LocalSettings local = LocalSettings.instance;
            if (!IsSignedIn) throw new InvalidOperationException("Not signed in to Google Drive.");
            TokenResponse token;
            try
            {
                token = await RequestToken(new Dictionary<string, string>
                {
                    { "refresh_token", local.googleRefreshToken },
                    { "client_id", local.googleClientId.Trim() },
                    { "client_secret", local.googleClientSecret.Trim() },
                    { "grant_type", "refresh_token" }
                });
            }
            catch (HttpException e) when (e.Code == 400 || e.Code == 401)
            {
                throw new InvalidOperationException("Google refused the saved sign-in (expired or revoked). Sign in again in the Distribution tab. " +
                                                    "If this happens every week, publish the OAuth consent screen: apps in Testing get 7-day tokens.");
            }
            Remember(token);
            return accessToken;
        }

        private static async Task<TokenResponse> RequestToken(Dictionary<string, string> form)
        {
            using (UnityWebRequest request = UnityWebRequest.Post(TokenUrl, form))
            {
                await Http.SendAsync(request);
                TokenResponse token = JsonUtility.FromJson<TokenResponse>(request.downloadHandler.text);
                if (string.IsNullOrEmpty(token.access_token)) throw new InvalidOperationException("Google: " + (token.error_description ?? token.error));
                return token;
            }
        }

        private static void Remember(TokenResponse token)
        {
            accessToken = token.access_token;
            expiresAt = DateTime.UtcNow.AddSeconds(Math.Max(60, token.expires_in - 60));
        }

        private static void Respond(HttpListenerContext context, string message)
        {
            byte[] body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><title>Android Build Supporter</title><p style=\"font-family:sans-serif\">{WebUtility.HtmlEncode(message)}</p>");
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = body.Length;
            context.Response.OutputStream.Write(body, 0, body.Length);
            context.Response.OutputStream.Close();
        }

        private static int FreePort()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            int port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            return port;
        }

        private static string RandomString(int bytes)
        {
            var data = new byte[bytes];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(data);
            return Base64Url(data);
        }

        private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
