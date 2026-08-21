using Echelon.Application.DTOs;
using Echelon.Infrastructure.Auth;
using Echelon.Infrastructure.Ingestion;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Echelon.Web.Controllers;

/// <summary>
/// What the ingestion is doing right now: the background workers, what has arrived, and the last poll
/// of each connection.
/// </summary>
/// <remarks>
/// The screen this feeds answers a question the logs answer badly - "is anything actually reading the
/// tracker and the VCS". A sweep that finds nothing and a sweep that cannot see anything produce the
/// same silence, and a webhook that stopped arriving looks like a quiet week until someone asks why a
/// task never appeared.
/// </remarks>
/// <param name="activity">This replica's recorder.</param>
[ApiController]
[Route("api/ingestion")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class IngestionController(IngestionActivity activity) : ControllerBase
{
    /// <summary>The ingestion status of the replica that answers this call.</summary>
    /// <returns>Workers, signals and connections, as of now.</returns>
    /// <remarks>
    /// Per replica, and not persisted: these are facts about a running process. With more than one
    /// replica the answer describes whichever one the load balancer picked - which is why the workers
    /// report whether they hold the sweep lease rather than only whether they are idle.
    /// </remarks>
    [HttpGet]
    public ActionResult<IngestionStatusDto> Get() => Ok(activity.Snapshot());
}
