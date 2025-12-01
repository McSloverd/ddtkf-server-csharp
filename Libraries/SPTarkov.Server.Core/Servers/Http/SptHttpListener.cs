using System.Collections.Immutable;
using System.IO.Compression;
using System.IO.Hashing;
using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Microsoft.Extensions.Primitives;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using LogLevel = SPTarkov.Server.Core.Models.Spt.Logging.LogLevel;

namespace SPTarkov.Server.Core.Servers.Http;

[Injectable]
public class SptHttpListener(
    HttpRouter httpRouter,
    IEnumerable<ISerializer> serializers,
    ISptLogger<SptHttpListener> logger,
    ISptLogger<RequestLogger> requestsLogger,
    JsonUtil jsonUtil,
    HttpResponseUtil httpResponseUtil
) : IHttpListener
{
    private readonly ISerializer[] _serializers = serializers as ISerializer[] ?? serializers.ToArray();
    private static readonly ImmutableHashSet<string> SupportedMethods = ["GET", "PUT", "POST"];

    public bool CanHandle(MongoId _, HttpContext context)
    {
        return SupportedMethods.Contains(context.Request.Method) && httpRouter.CanHandle(context);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        switch (context.Request.Method)
        {
            case "GET":
            {
                var response = await GetResponse(sessionId, context, null);

                // Another handler is already handling this, or no handler was found.
                if (response is null)
                {
                    return;
                }

                await SendResponse(sessionId, context.Request, context.Response, null, response);
                break;
            }
            // these are handled almost identically.
            case "POST":
            case "PUT":
            {
                // Contrary to reasonable expectations, the content-encoding is _not_ actually used to
                // determine if the payload is compressed. All PUT requests are, and POST requests without
                // debug = 1 are as well. This should be fixed.
                // let compressed = req.headers["content-encoding"] === "deflate";
                var requestIsCompressed =
                    !context.Request.Headers.TryGetValue("requestcompressed", out var compressHeader) || compressHeader != "0";
                var requestCompressed = context.Request.Method == "PUT" || requestIsCompressed;

                string body;

                if (requestCompressed)
                {
                    await using var deflateStream = new ZLibStream(context.Request.Body, CompressionMode.Decompress);
                    using var reader = new StreamReader(deflateStream, Encoding.UTF8);
                    body = await reader.ReadToEndAsync();
                }
                else
                {
                    using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                    body = await reader.ReadToEndAsync();
                }

                if (!requestIsCompressed)
                {
                    if (logger.IsLogEnabled(LogLevel.Debug))
                    {
                        logger.Debug(body);
                    }
                }

                var response = await GetResponse(sessionId, context, body);

                // Another handler is already handling this, or no handler was found.
                if (response is null)
                {
                    return;
                }

                await SendResponse(sessionId, context.Request, context.Response, body, response);
                break;
            }
        }
    }

    /// <summary>
    ///     Send HTTP response back to sender
    /// </summary>
    /// <param name="sessionID"> Player id making request </param>
    /// <param name="req"> Incoming request </param>
    /// <param name="resp"> Outgoing response </param>
    /// <param name="body"> Buffer </param>
    /// <param name="output"> Server generated response data</param>
    public async Task SendResponse(MongoId sessionID, HttpRequest req, HttpResponse resp, object? body, string output)
    {
        body ??= new object();

        if (IsDebugRequest(req))
        {
            // Send only raw response without transformation
            await SendJson(resp, output, sessionID);
            if (logger.IsLogEnabled(LogLevel.Debug))
            {
                logger.Debug($"Response: {output}");
            }

            LogRequest(req, output);
            return;
        }

        // Not debug, minority of requests need a serializer to do the job (IMAGE/BUNDLE/NOTIFY)
        ISerializer? serialiser = null;
        for (var i = 0; i < _serializers.Length; i++)
        {
            var s = _serializers[i];
            if (s.CanHandle(output))
            {
                serialiser = s;
                break;
            }
        }
        if (serialiser != null)
        {
            var bodyInfo = jsonUtil.Serialize(body);
            await serialiser.Serialize(sessionID, req, resp, bodyInfo);
        }
        else
        // No serializer can handle the request (majority of requests don't), zlib the output and send response back
        {
            await SendZlibJson(resp, output, sessionID);
        }

        LogRequest(req, output);
    }

    /// <summary>
    ///     Is request flagged as debug enabled
    /// </summary>
    /// <param name="req"> Incoming request </param>
    /// <returns> True if request is flagged as debug </returns>
    protected bool IsDebugRequest(HttpRequest req)
    {
        return req.Headers.TryGetValue("responsecompressed", out var value) && value == "0";
    }

    /// <summary>
    ///     Log request if enabled
    /// </summary>
    /// <param name="req"> Log request if enabled </param>
    /// <param name="output"> Output string </param>
    protected void LogRequest(HttpRequest req, string output)
    {
        if (ProgramStatics.ENTRY_TYPE() != EntryType.RELEASE && requestsLogger.IsLogEnabled(LogLevel.Info))
        {
            var log = new Response(req.Method, output);
            requestsLogger.Info($"RESPONSE={jsonUtil.Serialize(log)}");
        }
    }

    public async ValueTask<string> GetResponse(MongoId sessionId, HttpContext context, string? body)
    {
        var output = await httpRouter.GetResponse(context.Request, sessionId, body);

        // Route doesn't exist or response is not properly set up
        if (string.IsNullOrEmpty(output))
        {
            output = httpResponseUtil.GetBody<object?>(
                null,
                BackendErrorCodes.HTTPNotFound,
                $"UNHANDLED RESPONSE: {context.Request.Path.ToString()}"
            );
        }

        if (ProgramStatics.ENTRY_TYPE() != EntryType.RELEASE && requestsLogger.IsLogEnabled(LogLevel.Info))
        {
            // Parse quest info into object
            var log = new Request(context.Request.Method, new RequestData(context.Request.Path.ToString(), context.Request.Headers));
            requestsLogger.Info($"REQUEST={jsonUtil.Serialize(log)}");
        }

        return output;
    }

    public async Task SendJson(HttpResponse resp, string? output, MongoId sessionID)
    {
        resp.StatusCode = 200;
        resp.ContentType = "application/json";
        resp.Headers.Append("Set-Cookie", $"PHPSESSID={sessionID.ToString()}");
        if (!string.IsNullOrEmpty(output))
        {
            var headers = resp.GetTypedHeaders();
            headers.ETag = new EntityTagHeaderValue(new StringSegment(ComputeETag(output)));
            headers.LastModified = DateTimeOffset.UtcNow;
            resp.ContentLength = Encoding.UTF8.GetByteCount(output);
        }

        if (!string.IsNullOrEmpty(output))
        {
            await resp.WriteAsync(output);
        }
    }

    public async Task SendZlibJson(HttpResponse resp, string output, MongoId sessionID)
    {
        resp.StatusCode = 200;
        resp.ContentType = "application/json";
        resp.Headers.Append("Set-Cookie", $"PHPSESSID={sessionID.ToString()}");
        var headers = resp.GetTypedHeaders();
        headers.ETag = new EntityTagHeaderValue(new StringSegment(ComputeETag(output)));
        headers.LastModified = DateTimeOffset.UtcNow;
        resp.Headers.Append("Content-Encoding", "deflate");

        await using (var deflateStream = new ZLibStream(resp.Body, CompressionLevel.Fastest))
        {
            var enc = Encoding.UTF8;
            var byteCount = enc.GetByteCount(output);
            var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                enc.GetBytes(output, 0, output.Length, buffer, 0);
                await deflateStream.WriteAsync(new ReadOnlyMemory<byte>(buffer, 0, byteCount));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static string ComputeETag(string content)
    {
        var enc = Encoding.UTF8;
        var byteCount = enc.GetByteCount(content);
        var bytes = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            enc.GetBytes(content, 0, content.Length, bytes, 0);
            var hash = XxHash64.Hash(new ReadOnlySpan<byte>(bytes, 0, byteCount));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }
        return '"' + sb.ToString() + '"';
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private record Response(string Method, string jsonData);

    private record Request(string Method, object output);

    private record RequestData(string Url, object Headers);
}
