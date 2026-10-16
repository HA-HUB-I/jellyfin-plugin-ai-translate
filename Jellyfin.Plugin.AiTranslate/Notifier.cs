using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiTranslate;

/// <summary>Short on-screen messages to administrators' active sessions (where the client supports them).</summary>
public sealed class Notifier
{
    private readonly IServiceProvider _services;
    private readonly ILogger<Notifier> _logger;

    public Notifier(IServiceProvider services, ILogger<Notifier> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task SendAsync(string text, int timeoutMs = 10000)
    {
        try
        {
            // Resolved lazily: the session manager is not needed to build the subtitle provider.
            var sessions = _services.GetService<ISessionManager>();
            var users = _services.GetService<IUserManager>();
            if (sessions is null || users is null)
            {
                return;
            }

            var targets = sessions.Sessions
                .Where(s => s.IsActive && s.UserId != Guid.Empty
                    && s.SupportedCommands.Contains(GeneralCommandType.DisplayMessage)
                    && users.GetUserById(s.UserId) is { } u && u.HasPermission(PermissionKind.IsAdministrator))
                .ToList();
            foreach (var s in targets)
            {
                await sessions.SendMessageCommand(null!, s.Id, new MessageCommand { Header = "AI Translate", Text = text, TimeoutMs = timeoutMs }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AI Translate: could not send a message to the sessions");
        }
    }
}
