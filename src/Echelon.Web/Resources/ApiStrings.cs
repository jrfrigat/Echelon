namespace Echelon.Web.Resources;

/// <summary>
/// Marker for <c>IStringLocalizer&lt;ApiStrings&gt;</c>: names the resource set carrying the API's
/// user-facing messages. Deliberately has no members - the strings live in the .resx pair beside
/// this file and are looked up by key.
/// </summary>
/// <remarks>
/// The namespace has to stay in step with the folder, and the folder with this type's name.
/// Two SDK/framework conventions meet here and both are silent when they disagree:
/// <list type="bullet">
/// <item>
/// EmbeddedResourceUseDependentUponConvention names ApiStrings.resx after the namespace of the
/// same-named source file next to it - this one - giving
/// <c>Echelon.Web.Resources.ApiStrings</c>.
/// </item>
/// <item>
/// ResourceManagerStringLocalizerFactory, with no ResourcesPath configured, looks up a type's
/// full name - also <c>Echelon.Web.Resources.ApiStrings</c>.
/// </item>
/// </list>
/// They agree only because both sides read the same namespace. Move this type, rename the folder,
/// or set ResourcesPath, and the two names drift apart: nothing throws, the factory simply fails
/// to find the resource and every message degrades to its own key ("Vcs_NameTaken" on screen).
/// </remarks>
public sealed class ApiStrings;
