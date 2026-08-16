using System.Runtime.CompilerServices;

// GitLabProvider is internal because nothing outside this assembly should construct an adapter
// directly -- the factory owns that, and it is what decrypts the token. But the adapter is also
// where GitLab's wire format is turned into the domain's, and that translation has already shipped
// one silent defect: dates bound to DateTime arrived as Kind=Local, which SQL Server stores at the
// wrong instant and PostgreSQL refuses outright, so the bug was invisible until the second database
// ran. That has to be reachable from a test, hence this rather than making the type public.
[assembly: InternalsVisibleTo("Echelon.UnitTests")]
