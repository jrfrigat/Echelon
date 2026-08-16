namespace Echelon.Application.Contracts.Messages;

/// <summary>
/// Marks a type as a message that travels over the bus.
/// </summary>
/// <remarks>
/// The routing map is built from this: <c>MapAssemblyDerivedFrom&lt;IMessage&gt;</c> routes exactly
/// the types that implement it to the Core queue, and nothing else. Before it, routing mapped the
/// whole Application assembly - every DTO, exception and enum alongside the six real messages -
/// which was harmless but untrue, and it showed: the bus logged each of them as a mapped
/// destination at startup. A message now says so in its declaration, and adding one without this
/// interface leaves it unroutable at compile time rather than mis-mapped at runtime.
/// </remarks>
public interface IMessage;
