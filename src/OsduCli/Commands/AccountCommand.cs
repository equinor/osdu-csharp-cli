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
            var cached = await context.Msal.GetCachedUsernamesAsync(token);
            // The resolved choice, not just the flag: a `username` in the profile counts
            // too, and reading only --user here reported the wrong account as in use.
            var selected = context.Username;

            if (cached.Count == 0)
            {
                context.Output.WriteMessage(
                    "Not signed in. The next command that reaches a service will open a browser.");
                return 0;
            }

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

            context.Output.Write(rows.ToJsonString(), OutputSpec.Table(
                null, ("Account", "account"), ("In use", "inUse")));

            if (selected is not null
                && !cached.Contains(selected, StringComparer.OrdinalIgnoreCase))
            {
                // Worth saying plainly: the next command will open a browser, and if the user
                // signs in as somebody else it will fail rather than quietly use them.
                context.Output.WriteMessage(
                    $"{selected} is selected but not signed in on this machine. "
                    + "The next command that reaches a service will prompt for it.");
            }
            else if (effective is null)
            {
                context.Output.WriteMessage(
                    "No account selected — commands will report the ambiguity rather than "
                    + "guess. Use --user <account>, or set `username` in your config profile.");
            }

            return 0;
        }, cancellationToken));

        command.Subcommands.Add(list);
        return command;
    }
}
