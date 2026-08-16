using System.Runtime.CompilerServices;

// ArchiveRunner is internal because nothing outside this assembly should be able to start deleting
// rows. It is also the one component here whose mistakes are unrecoverable, so it has to be
// testable - hence this, rather than making it public to be reachable from a test.
[assembly: InternalsVisibleTo("ReleaseOrchestrator.UnitTests")]
