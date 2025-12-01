using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Servers.Http;
using SPTarkov.Server.Core.Services;
using System.Linq;

namespace SPTarkov.Server.Core.Servers;

[Injectable(InjectionType.Singleton)]
public class HttpServer(
    ConfigServer configServer,
    WebSocketServer webSocketServer,
    ProfileActivityService profileActivityService,
    IEnumerable<IHttpListener> httpListeners
)
{
    protected readonly HttpConfig HttpConfig = configServer.GetConfig<HttpConfig>();
    private readonly IHttpListener[] _httpListeners = httpListeners as IHttpListener[] ?? httpListeners.ToArray();

    public async Task HandleRequest(HttpContext context, RequestDelegate next)
    {
        if (context.WebSockets.IsWebSocketRequest && webSocketServer.CanHandle(context))
        {
            await webSocketServer.OnConnection(context);
            return;
        }

        // Use default empty mongoId if not found in cookie
        var sessionId = context.Request.Cookies.TryGetValue("PHPSESSID", out var sessionIdString)
            ? new MongoId(sessionIdString)
            : MongoId.Empty();

        if (!string.IsNullOrEmpty(sessionIdString))
        {
            profileActivityService.SetActivityTimestamp(sessionId);
        }

        var listener = FindListener(sessionId, context);

        if (listener != null)
        {
            await listener.Handle(sessionId, context);
        }
        else
        {
            await next(context);
        }
    }

    public string ListeningUrl()
    {
        return $"https://{HttpConfig.Ip}:{HttpConfig.Port}";
    }

    private IHttpListener? FindListener(MongoId sessionId, HttpContext context)
    {
        for (var i = 0; i < _httpListeners.Length; i++)
        {
            var listener = _httpListeners[i];
            if (listener.CanHandle(sessionId, context))
            {
                return listener;
            }
        }
        return null;
    }
}
