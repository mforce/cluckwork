namespace Cluckwork.Infrastructure.Repositories;

// #512 US4 — tags on the reads whose CONTRACT is a shape ("one grouped read per
// page", "one LEFT JOIN", "this aggregate is bounded to the ids I returned").
// A wrong shape can return right data, so no assertion on a response proves
// those properties; the statement that went to the database can. Tagging the read
// gives a test one handle on that statement — count executions of the tag and you
// have counted the read, without pinning EF's generated aliases (which an EF
// upgrade renames, reddening a performance guard with nothing broken).
//
// Placement is load-bearing: the tag sits on the FINAL query, immediately before
// materialisation. EF folds tags into a leading comment block and Npgsql reports
// CommandText without leading comments, so a tag applied where another operator
// still composes onto the query may never reach the text an interceptor inspects.
// NamedRowProjectionTests therefore asserts each tag actually reaches the
// interceptor (ProbeSeesEveryTaggedRead): a tag that stops arriving reddens that
// guard instead of silently turning every count below into "zero reads, no N+1".
//
// Plain internal constants, nothing else — the tags are read-path annotations and
// no production code parses them.
internal static class ReferenceMarkers
{
    // The scoped bulk flock display-name read (FlockRepository).
    public const string FlockReference = "cluckwork512-flock-reference";

    // The scoped bulk customer display-name read (CustomerRepository).
    public const string CustomerReference = "cluckwork512-customer-reference";

    // Net-birds-per-flock aggregate bounded to returned flock ids
    // (BirdMovementRepository).
    public const string MovementAggregate = "cluckwork512-movement-aggregate";

    // The assignment-to-flock left-join projection
    // (UserRoleAssignmentRepository).
    public const string AssignmentProjection = "cluckwork512-assignment-projection";
}
