using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace ThanhDV.AndroidBuildSupporter
{
    internal sealed class HttpException : Exception
    {
        public readonly long Code;

        public HttpException(UnityWebRequest request)
            : base(Describe(request))
        {
            Code = request.responseCode;
        }

        private static string Describe(UnityWebRequest request)
        {
            string body = request.downloadHandler?.text ?? "";
            if (body.Length > 300) body = body.Substring(0, 300) + "…";
            return $"{request.method} {new Uri(request.url).GetLeftPart(UriPartial.Path)} → {request.responseCode} {request.error} {body}".Trim();
        }
    }

    internal static class Http
    {
        /// <summary>Sends the request without blocking the Editor; throws <see cref="HttpException"/> on any non-2xx answer.</summary>
        public static async Task<UnityWebRequest> SendAsync(UnityWebRequest request, Action<float> uploadProgress = null)
        {
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                uploadProgress?.Invoke(request.uploadProgress);
                await Task.Delay(100);
            }
            if (request.responseCode < 200 || request.responseCode >= 300) throw new HttpException(request);
            return request;
        }

        public static UnityWebRequest Json(string url, string method, string json, string accessToken = null)
        {
            var request = new UnityWebRequest(url, method)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer()
            };
            request.SetRequestHeader("Content-Type", "application/json; charset=UTF-8");
            if (accessToken != null) request.SetRequestHeader("Authorization", "Bearer " + accessToken);
            return request;
        }

        public static UnityWebRequest Get(string url, string accessToken)
        {
            UnityWebRequest request = UnityWebRequest.Get(url);
            request.SetRequestHeader("Authorization", "Bearer " + accessToken);
            return request;
        }
    }
}
