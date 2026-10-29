using PolyMount.Core;

namespace PolyMount.Tray;

/// <summary>How an answer from a source check reads in a window: one sentence and the colour it is
/// written in.
///
/// It is here rather than in either window because the same question is asked in two places — the
/// new-instance page and the layer panel of the editor — and two copies of these sentences would
/// drift into two vocabularies for one answer. The pass sentence carries the two numbers that make
/// it evidence rather than a green tick: how much the source listed, and how long it took.</summary>
internal static class SourceCheckText
{
    public static (string Text, string Color) Describe(SourceCheckResult result) => result.Ok
        ? (I18n.F("credential.check.ok", result.Entries, result.Milliseconds / 1000), TrayTheme.Tokens.Success)
        : (I18n.F(result.Kind is SourceCheckKinds.Auth or SourceCheckKinds.Origin
                ? "credential.check.refused"     // the account is the thing to change
                : "credential.check.error",      // the address, the certificate, the network
            result.Message),
            TrayTheme.Tokens.Danger);
}
