using System.CommandLine;
using System.Text.Json.Nodes;
using Equinor.OsduCli.Runtime;

namespace Equinor.OsduCli.Commands;

/// <summary>
/// <c>osducs account list</c> — the accounts this machine is signed in as.
/// </summary>
/// <remarks>
/// Without this, <c>--user</c> is a flag you can only use if you already know the answer.
/// The token cache is an opaque encrypted blob, so there is otherwise no way to find out
/// which accounts are in it, or which one a bare command would have picked.
///
/// Hand-written because it reports on the CLI's own auth state rather than calling a service,
/// which no manifest can express.
/// </remarks>
public static class AccountCommand
{
    public static Command Build()
    {
        var command = new Command("account", "Inspect which accounts you are signed in as.");
        var list = new Command("list", "List the accounts in this machine's token cache.");

        list.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, token) =>
        {
            if (context.Azure is not null)
            {
                // Which source answered and as whom is only known from a token, so this one
                // fetches it, which `config show` does not.
                var who = AzureTokenProvider.Identify(await context.Azure.GetTokenAsync(token));
                context.Output.Write(
                    new JsonArray(new JsonObject { ["account"] = who.Description, ["inUse"] = "yes" }).ToJsonString(), Spec);
                // `az login` changes only the Azure CLI's answer, and the other sources come before
                // it. The token says for certain that the Azure CLI answered only when it was
                // issued to the Azure CLI itself; a service principal signed in to the Azure CLI,
                // as in a pipeline, looks like any other application. So otherwise this does not
                // say which source it was.
                context.Output.WriteNote(who.IssuedToAzureCli
                    ? "This profile signs in through Azure, here through the Azure CLI. Choose another account with `az login`."
                    : "This profile signs in through Azure, as the first of these that is there: AZURE_* variables, "
                      + "a workload identity, a managed identity, or the Azure CLI. `az login` changes it only when "
                      + "the Azure CLI is the one answering.");
                return 0;
            }

            if (context.Msal is null)
            {
                // An application sign-in has no accounts, and the interactive cache, which
                // may well hold some, is not what this profile uses.
                context.Output.Write("[]", Spec);
                context.Output.WriteNote(
                    $"This profile signs in as the application {context.Config.ClientId} with a "
                    + "client secret, so there is no account to choose.");
                return 0;
            }

            var cached = await context.Msal.GetCachedUsernamesAsync(token);
            // The resolved choice, not just the flag: a `user` in the profile counts
            // too, and reading only --user here reported the wrong account as in use.
            var selected = context.Username;

            // Which one a command would actually use, by the same rule the provider applies:
            // an explicit --user, else the only cached account, else nothing — because more
            // than one with no choice made is exactly the case that refuses to guess.
            var effective = selected ?? (cached.Count == 1 ? cached[0] : null);

            var rows = new JsonArray();
            foreach (var username in cached)
            {
                rows.Add(new JsonObject
                {
                    ["account"] = username,
                    ["inUse"] = string.Equals(username, effective, StringComparison.OrdinalIgnoreCase)
                        ? "yes"
                        : "",
                });
            }

            // Written even when empty, so that --output json always yields parseable output
            // rather than nothing at all. The advisories below go to stderr for the same
            // reason from the other side: they must survive JSON mode, where the rows cannot
            // express "selected but not signed in" — every row just reads as not in use.
            context.Output.Write(rows.ToJsonString(), Spec);

            if (cached.Count == 0)
            {
                context.Output.WriteNote(
                    "Not signed in. The next command that reaches a service will open a browser.");
            }
            else if (selected is not null
                && !cached.Contains(selected, StringComparer.OrdinalIgnoreCase))
            {
                // Worth saying plainly: the next command will open a browser, and if the user
                // signs in as somebody else it will fail rather than quietly use them.
                context.Output.WriteNote(
                    $"{selected} is selected but not signed in on this machine. "
                    + "The next command that reaches a service will prompt for it.");
            }
            else if (effective is null)
            {
                context.Output.WriteNote(
                    "No account selected — commands will report the ambiguity rather than "
                    + "guess. Use --user <account>, or set `user` in your config profile.");
            }

            return 0;
        }, cancellationToken));

        command.Subcommands.Add(list);
        return command;
    }

    private static readonly OutputSpec Spec = OutputSpec.Table(
        null, ("Account", "account"), ("In use", "inUse"));
}
