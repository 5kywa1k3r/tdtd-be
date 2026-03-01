using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using tusdotnet;
using tusdotnet.Interfaces;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;

namespace tdtd_be.Uploads;

public static class TusUploadMap
{
    public static void MapTusUploads(this WebApplication app)
    {
        var opt = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<UploadOptions>>().Value;

        var maxBytesInt = opt.MaxUploadBytes > int.MaxValue ? int.MaxValue : (int)opt.MaxUploadBytes;

        app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/api/uploads"), tusApp =>
        {
            var store = app.Services.GetRequiredService<ITusStore>();

            tusApp.UseTus(httpContext => new DefaultTusConfiguration
            {
                UrlPath = "/api/uploads",
                Store = store,
                MaxAllowedUploadSizeInBytes = maxBytesInt,

                Events = new Events
                {
                    OnAuthorizeAsync = async authorizeCtx =>
                    {
                        var req = authorizeCtx.HttpContext.Request;

                        if (!req.Headers.TryGetValue("Upload-Token", out var tok) || string.IsNullOrWhiteSpace(tok))
                        {
                            authorizeCtx.FailRequest("Missing Upload-Token");
                            return;
                        }

                        var tokens = authorizeCtx.HttpContext.RequestServices.GetRequiredService<UploadTokenService>();
                        var payload = tokens.Validate(tok!);
                        if (payload == null)
                        {
                            authorizeCtx.FailRequest("Invalid Upload-Token");
                            return;
                        }

                        // Enforce size on CREATE
                        if (HttpMethods.IsPost(req.Method))
                        {
                            if (!req.Headers.TryGetValue("Upload-Length", out var lenVals) ||
                                !long.TryParse(lenVals.ToString(), out var uploadLen))
                            {
                                authorizeCtx.FailRequest("Missing/Invalid Upload-Length");
                                return;
                            }

                            if (uploadLen != payload.Length)
                            {
                                authorizeCtx.FailRequest("Upload-Length mismatch");
                                return;
                            }

                            if (uploadLen > opt.MaxUploadBytes)
                            {
                                authorizeCtx.FailRequest("Upload too large");
                                return;
                            }
                        }

                        await Task.CompletedTask;
                    },

                    OnFileCompleteAsync = async completeCtx =>
                    {
                        var finalize = httpContext.RequestServices.GetRequiredService<UploadFinalizeService>();
                        await finalize.FinalizeAsync(completeCtx);
                    }
                }
            });
        });
    }
}