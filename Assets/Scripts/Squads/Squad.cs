using System.Collections.Generic;
using UnityEngine;

[DefaultExecutionOrder(-10)]
public class Squad : MonoBehaviour
{
    [Header("Tick")]
    [SerializeField] private float tickInterval = 0.1f;

    [Header("Engagement")]
    [Tooltip("Enemies within this distance of the squad anchor are part of THIS squad's battle. " +
             "Doubles as the leash: members are reassigned every tick from this set, so a fighter " +
             "chasing a target past this radius gets recalled.")]
    [SerializeField] private float engagementRadius = 60f;

    [Tooltip("Max time (s) added to a fighter's order timestamp on Holdout->Engage transition. " +
             "Causes break-formation moments to look staggered rather than synchronous.")]
    [SerializeField] private float breakStaggerMax = 0.15f;

    [Tooltip("Time (s) between adjacent members' earliest missile-fire times within a volley. " +
             "Spreads the initial volley over (members.Count - 1) * missileStagger seconds; weapon " +
             "cooldowns then keep subsequent volleys staggered too. Zero disables stagger.")]
    [SerializeField] private float missileStagger = 0.25f;

    [Header("Cruise (no enemies in radius)")]
    [Tooltip("Squad cruise speed used as squadVelocity hint while cruising toward enemies.")]
    [SerializeField] private float cruiseSpeed = 10f;

    [Tooltip("Distance the slot is shifted forward (in cruise direction) while cruising. " +
             "Forces fighters to perpetually chase, which keeps the formation moving toward enemies " +
             "rather than settling at the centroid.")]
    [SerializeField] private float cruiseLookahead = 5f;

    [Header("Formation (vee)")]
    [SerializeField] private float formationSpacing = 1.5f;

    [Header("Team")]
    [Tooltip("Tag of the team this squad belongs to. Used to look up enemies via SceneManager.")]
    public string teamTag;

    private readonly List<FighterController> members = new List<FighterController>();

    private ShipBase leader;

    public Vector2 anchorPos { get; private set; }
    public Vector2 anchorVelocity { get; private set; }
    public Vector2 anchorHeading { get; private set; } = Vector2.up;

    private float nextTickTime;

    private float engagementStartTime = -1f;

    // Reused per-tick scratch buffers.
    private readonly List<ShipBase> engagementSet = new List<ShipBase>();
    private readonly List<int> assignmentCounts = new List<int>();

    private void Start()
    {
        nextTickTime = Time.time + Random.Range(0f, tickInterval);
    }

    public void RegisterMember(FighterController brain)
    {
        if (brain == null) return;
        if (members.Contains(brain)) return;
        members.Add(brain);
        brain.squad = this;
    }

    public void UnregisterMember(FighterController brain)
    {
        if (brain == null) return;
        if (members.Remove(brain) && brain.squad == this)
            brain.squad = null;
    }

    /// <summary>
    /// Makes the squad form on this ship. The leader holds the vee tip and the other members
    /// trail behind it. It may be one of the squad's own AI members (which then navigates for
    /// the squad) or an external ship such as the player's. Passing null (or leader death)
    /// causes the first surviving member to be promoted on the next tick.
    /// </summary>
    public void SetLeader(ShipBase ship)
    {
        leader = ship;
    }

    public ShipBase GetLeader() => leader;

    public IReadOnlyList<FighterController> GetMembers() => members;

    /// <summary>Index of a fighter in the squad's slot ordering. -1 if not a member.</summary>
    public int GetSlotIndex(FighterController brain) => members.IndexOf(brain);

    private void Update()
    {
        if (Time.time < nextTickTime) return;
        nextTickTime = Time.time + tickInterval;
        Tick();
    }

    private void Tick()
    {
        if (leader != null && !leader.gameObject.activeInHierarchy)
            leader = null;

        PruneDeadMembers();
        if (members.Count == 0)
        {
            enabled = false;
            return;
        }

        if (leader == null)
            leader = members[0].ship;
        int leaderMemberIndex = FindLeaderMemberIndex();

        UpdateAnchor();
        BuildEngagementSet();

        bool engaged = engagementSet.Count > 0;

        // Anchor missile-stagger times on the moment we first entered this engagement so per-member
        // fire windows stay stable across squad ticks. Weapon cooldowns then carry the stagger forward.
        if (engaged && engagementStartTime < 0f) engagementStartTime = Time.time;
        else if (!engaged) engagementStartTime = -1f;

        bool cruising = false;
        Vector2 leaderCruiseSlot = anchorPos;
        Vector2 leaderCruiseVelocity = anchorVelocity;
        Vector2 leaderCruiseHeading = anchorHeading;
        if (!engaged && leaderMemberIndex >= 0)
        {
            Vector2 cruiseDir = FindCruiseDirection();
            if (cruiseDir.sqrMagnitude > 0.0001f)
            {
                cruising = true;
                leaderCruiseSlot = anchorPos + cruiseDir * cruiseLookahead;
                leaderCruiseVelocity = cruiseDir * cruiseSpeed;
                leaderCruiseHeading = cruiseDir;
            }
        }

        // Per-target assignment counts, capped so we spread across enemies when we have enough fighters.
        assignmentCounts.Clear();
        for (int i = 0; i < engagementSet.Count; i++) assignmentCounts.Add(0);
        int assignmentCap = engaged
            ? Mathf.Max(1, Mathf.CeilToInt((float)members.Count / engagementSet.Count))
            : 0;

        int formationIndex = 0;
        for (int i = 0; i < members.Count; i++)
        {
            FighterController member = members[i];

            Vector2 slotWorld;
            Vector2 orderVelocity;
            Vector2 orderHeading;
            if (i == leaderMemberIndex)
            {
                slotWorld = cruising ? leaderCruiseSlot : anchorPos;
                orderVelocity = cruising ? leaderCruiseVelocity : anchorVelocity;
                orderHeading = cruising ? leaderCruiseHeading : anchorHeading;
            }
            else
            {
                slotWorld = ComputeSlotWorld(formationIndex);
                orderVelocity = anchorVelocity;
                orderHeading = anchorHeading;
                formationIndex++;
            }

            ShipBase assignedTarget = null;
            if (engaged)
                assignedTarget = AssignTarget(member, assignmentCap);

            FighterOrder previous = member.currentOrder;
            FighterMode mode = (assignedTarget != null) ? FighterMode.Engage : FighterMode.Holdout;

            float orderTimestamp;
            if (mode == FighterMode.Engage && previous.mode != FighterMode.Engage)
                orderTimestamp = Time.time + Random.Range(0f, breakStaggerMax);
            else
                orderTimestamp = Time.time;

            float missileFireTime = (mode == FighterMode.Engage)
                ? engagementStartTime + i * missileStagger
                : float.MaxValue;

            FighterOrder order = new FighterOrder
            {
                mode = mode,
                slotPosition = slotWorld,
                squadVelocity = orderVelocity,
                squadHeading = orderHeading,
                target = assignedTarget,
                authorizeMissile = (mode == FighterMode.Engage),
                orderTimestamp = orderTimestamp,
                missileFireTime = missileFireTime,
            };
            member.currentOrder = order;
        }
    }

    /// <summary>Index of the leader in the member list, or -1 when the leader is external.</summary>
    private int FindLeaderMemberIndex()
    {
        if (leader == null) return -1;
        GameObject leaderObj = leader.gameObject;
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i].gameObject == leaderObj) return i;
        }
        return -1;
    }

    /// <summary>
    /// Returns a unit vector from the anchor toward the nearest live enemy on the opposing team,
    /// ignoring engagementRadius. Returns Vector2.zero when no enemies exist.
    /// </summary>
    private Vector2 FindCruiseDirection()
    {
        if (SceneManager.Instance == null || string.IsNullOrEmpty(teamTag)) return Vector2.zero;
        List<ShipBase> enemies = SceneManager.Instance.GetLiveEnemies(teamTag);

        ShipBase best = null;
        float bestDistSqr = float.PositiveInfinity;
        for (int i = 0; i < enemies.Count; i++)
        {
            ShipBase enemy = enemies[i];
            if (enemy == null || !enemy.gameObject.activeInHierarchy) continue;
            float distSqr = (enemy.GetRigidBody().position - anchorPos).sqrMagnitude;
            if (distSqr < bestDistSqr)
            {
                bestDistSqr = distSqr;
                best = enemy;
            }
        }

        if (best == null) return Vector2.zero;

        Vector2 toEnemy = best.GetRigidBody().position - anchorPos;
        float distSqrToEnemy = toEnemy.sqrMagnitude;
        if (distSqrToEnemy < 0.0001f) return Vector2.zero;
        return toEnemy / Mathf.Sqrt(distSqrToEnemy);
    }

    private void PruneDeadMembers()
    {
        for (int i = members.Count - 1; i >= 0; i--)
        {
            FighterController m = members[i];
            if (m == null || !m.gameObject.activeInHierarchy)
                members.RemoveAt(i);
        }
    }

    // Leader is guaranteed non-null here: Tick promotes a member before updating the anchor.
    private void UpdateAnchor()
    {
        Rigidbody2D lrb = leader.GetRigidBody();
        anchorPos = lrb.position;
        anchorVelocity = lrb.linearVelocity;
        Vector2 up = leader.transform.up;
        if (up.sqrMagnitude > 0.0001f)
            anchorHeading = up.normalized;
    }

    private void BuildEngagementSet()
    {
        engagementSet.Clear();
        if (SceneManager.Instance == null || string.IsNullOrEmpty(teamTag)) return;

        List<ShipBase> enemies = SceneManager.Instance.GetLiveEnemies(teamTag);
        float radiusSqr = engagementRadius * engagementRadius;

        for (int i = 0; i < enemies.Count; i++)
        {
            ShipBase enemy = enemies[i];
            if (enemy == null || !enemy.gameObject.activeInHierarchy) continue;
            float distSqr = (enemy.GetRigidBody().position - anchorPos).sqrMagnitude;
            if (distSqr <= radiusSqr)
                engagementSet.Add(enemy);
        }
    }

    private ShipBase AssignTarget(FighterController member, int cap)
    {
        Vector2 memberPos = member.rb.position;
        ShipBase best = null;
        int bestIndex = -1;
        float bestDistSqr = float.PositiveInfinity;

        for (int i = 0; i < engagementSet.Count; i++)
        {
            if (assignmentCounts[i] >= cap) continue;
            ShipBase candidate = engagementSet[i];
            float distSqr = (candidate.GetRigidBody().position - memberPos).sqrMagnitude;
            if (distSqr < bestDistSqr)
            {
                bestDistSqr = distSqr;
                best = candidate;
                bestIndex = i;
            }
        }

        // Cap may exclude every target on a tick where members.Count isn't divisible evenly;
        // fall back to absolute nearest so no member is left unassigned while enemies exist.
        if (best == null && engagementSet.Count > 0)
        {
            for (int i = 0; i < engagementSet.Count; i++)
            {
                ShipBase candidate = engagementSet[i];
                float distSqr = (candidate.GetRigidBody().position - memberPos).sqrMagnitude;
                if (distSqr < bestDistSqr)
                {
                    bestDistSqr = distSqr;
                    best = candidate;
                    bestIndex = i;
                }
            }
        }

        if (bestIndex >= 0) assignmentCounts[bestIndex]++;
        return best;
    }

    /// <summary>
    /// Vee formation hanging off the leader's tail: the leader holds raw slot 0 (the tip), so
    /// follower <paramref name="formationIndex"/> f takes raw slot f+1 behind it, in world
    /// space, oriented by the leader's heading.
    /// </summary>
    private Vector2 ComputeSlotWorld(int formationIndex)
    {
        Vector2 local = VeeSlotRaw(formationIndex + 1, formationSpacing);

        Vector2 forward = anchorHeading;
        Vector2 right = new Vector2(forward.y, -forward.x);
        Vector2 worldOffset = right * local.x + forward * local.y;
        return anchorPos + worldOffset;
    }

    /// <summary>
    /// Vee local-space slot offset (X = perpendicular, Y = along the heading direction).
    /// Result is shifted so that the geometric centroid of all <paramref name="slotCount"/>
    /// slots is at (0, 0). Slot 0 ends up forward of origin; trailing slots ramp back.
    /// </summary>
    public static Vector2 ComputeVeeSlotCentered(int slotIndex, int slotCount, float spacing)
    {
        Vector2 raw = VeeSlotRaw(slotIndex, spacing);
        Vector2 centroidOffset = Vector2.zero;
        for (int i = 0; i < slotCount; i++) centroidOffset += VeeSlotRaw(i, spacing);
        if (slotCount > 0) centroidOffset /= slotCount;
        return raw - centroidOffset;
    }

    private static Vector2 VeeSlotRaw(int slotIndex, float spacing)
    {
        if (slotIndex == 0) return Vector2.zero;
        int row = (slotIndex + 1) / 2;
        int side = ((slotIndex - 1) % 2 == 0) ? -1 : 1;
        return new Vector2(side * row * spacing, -row * spacing);
    }
}
