using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace web
{
    public class JsonMiddleware
    {
        private static readonly PathString _path = new PathString("/json");
        private const int _bufferSize = 27;

        private readonly RequestDelegate _next;

        public JsonMiddleware(RequestDelegate next) => _next = next;

        public Task Invoke(HttpContext httpContext)
        {
            if (httpContext.Request.Path.StartsWithSegments(_path, StringComparison.Ordinal))
            {
                httpContext.Response.StatusCode = 200;
                httpContext.Response.ContentType = "application/json";
                httpContext.Response.ContentLength = _bufferSize;

                return JsonSerializer.SerializeAsync<JsonMessage>(httpContext.Response.Body, new JsonMessage { message = "Hello, World!" });
            }
            else
            {
                return _next(httpContext);
            }
        }
    }

    public static class JsonMiddlewareExtensions
    {
        public static IApplicationBuilder UseJson(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<JsonMiddleware>();
        }
    }

    public struct JsonMessage
    {
        public string message { get; set; }
    }
}
