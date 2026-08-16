namespace Echelon.Infrastructure.Auth;

/// <summary>
/// Bound from the <c>Authorization</c> configuration section.
///
/// Permissions live in the database and are granted under config.edit - which nobody holds
/// on a fresh install, and which is itself required to grant config.edit. Without an escape
/// hatch the deployment is unusable and the only way in is editing the database by hand.
/// </summary>
public class PermissionBootstrapOptions
{
    /// <summary>
    /// Entra object ids that receive every permission, bypassing the database. Bootstrap only:
    /// remove the setting once real grants exist. It widens no privilege boundary - whoever
    /// sets environment variables already holds the connection string.
    /// </summary>
    public string[] BootstrapAdminObjectIds { get; set; } = [];
}
