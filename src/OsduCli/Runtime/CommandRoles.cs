using System.CommandLine;
using System.Runtime.CompilerServices;

namespace Equinor.OsduCli.Runtime;

/// <summary>
/// The roles a command needs, as its service documents them, for <c>--help</c> to show.
/// </summary>
/// <remarks>
/// <para>The same text a refused request prints, which was the only place it appeared: nothing
/// said before running <c>record purge</c> that it needs <c>service.storage.admin</c>. Kept
/// beside the command rather than on it, since System.CommandLine has nowhere for it.</para>
///
/// <para>These are the roles the specs document, which is not always what a deployment
/// enforces; ADME, for one, refuses some requests with 401 where the spec implies 403.</para>
/// </remarks>
public static class CommandRoles
{
    private sealed record Requirement(string Roles, bool Operator);

    private static readonly ConditionalWeakTable<Command, Requirement> Required = new();

    /// <summary>Records that <paramref name="command"/> needs <paramref name="roles"/>.</summary>
    /// <param name="command">The command.</param>
    /// <param name="roles">The roles, as the service documents them, e.g. <c>users.datalake.ops or users.datalake.admins</c>.</param>
    /// <param name="operatorRole">Whether every one of them is an admin or ops role, beyond what a data user holds.</param>
    public static void Set(Command command, string roles, bool operatorRole = false) =>
        Required.AddOrUpdate(command, new Requirement(roles, operatorRole));

    /// <summary>What <paramref name="command"/> needs, as a line for help, or null when nothing is documented.</summary>
    public static string? Describe(Command command) =>
        Required.TryGetValue(command, out var requirement)
            ? requirement.Operator ? $"{requirement.Roles} (an operator role)" : requirement.Roles
            : null;
}
