using System.Runtime.CompilerServices;

namespace Lakehold.Engine.Tests;

/// <summary>
///     Loads <c>.env</c> into the process environment before any test runs.
/// </summary>
/// <remarks>
///     <para>
///         The integration suites are gated on environment variables — <c>LAKEHOLD_TEST_POSTGRES</c>,
///         <c>LAKEHOLD_TEST_S3_ENDPOINT</c>, and friends — and skip when they are unset. Before this,
///         opting in meant exporting five variables into whichever shell happened to launch the
///         tests, which an IDE test runner does not inherit. The usual outcome was a suite that
///         silently skipped in the IDE and ran from the terminal, which is the wrong way round for
///         noticing a regression.
///     </para>
///     <para>
///         A module initializer runs before the first test in the assembly, which is early enough:
///         the gates are read in fixture construction, not at static-field initialisation.
///     </para>
///     <para>
///         Loading is best-effort and non-overwriting. A missing <c>.env</c> is the normal state on
///         CI and simply leaves the suites skipped, and a variable already present in the real
///         environment always wins, so CI secrets are never shadowed by a developer's local file.
///     </para>
/// </remarks>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Load()
    {
        try
        {
            // The test binary runs from bin/Debug/<tfm>, so the file is several levels above it.
            // TraversePath walks up to the repository root rather than pinning a relative depth that
            // a target-framework or configuration change would silently break.
            DotNetEnv.Env.TraversePath().Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable .env must not take down the whole assembly's test run. The gated suites
            // will simply skip, which is the same outcome as not having the file at all.
        }
    }
}
