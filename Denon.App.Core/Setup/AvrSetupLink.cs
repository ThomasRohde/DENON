using Denon.App.Core.Heos;
using Denon.App.Core.Models;

namespace Denon.App.Core.Setup;

/// <summary>The real receiver behind <see cref="IReceiverSetupLink"/>: port 23 through <see cref="DenonAvrClient"/>.</summary>
public sealed class AvrSetupLink(DenonAvrClient client, ReceiverSettings settings, string model) : IReceiverSetupLink
{
    public async Task<SetupContext> GetContextAsync(CancellationToken cancellationToken)
    {
        var (power, zone, input, mode) = await client.GetSetupContextAsync(settings, cancellationToken);
        return new SetupContext(model, input, mode, power, zone, settings.Host);
    }

    public Task<IReadOnlyList<string>> QueryAsync(AvrSetupCommand command, Func<string, bool> answerLine, CancellationToken cancellationToken) =>
        client.QuerySetupAsync(settings, command, answerLine, cancellationToken);

    public Task SendAsync(AvrSetupCommand command, CancellationToken cancellationToken) =>
        client.SendSetupAsync(settings, command, cancellationToken);
}
