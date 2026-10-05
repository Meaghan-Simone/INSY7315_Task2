using UncoveringGreatnessCRM.Domain;

namespace UncoveringGreatnessCRM.Security;

/// <summary>
/// The single rule for lead visibility: admins see every lead, everyone else sees only the leads assigned to them.
/// Every query that reads leads on behalf of a signed-in user goes through <see cref="VisibleTo"/>, so the web UI,
/// the JSON API, search, the dashboard, exports and the pipeline can never disagree.
/// </summary>
public static class LeadScope
{
    public static IQueryable<Lead> VisibleTo(this IQueryable<Lead> leads, ICurrentUser me)
    {
        if (me.IsAdmin) return leads;
        var myId = me.Id;
        return leads.Where(l => l.AssignedToId == myId);
    }
}
