using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class FighterController : AIControllerBase
{
    [Header("Tick")]
    [SerializeField] private float tickInterval = 0.1f;

    [Header("Combat")]
    [SerializeField] private float missileEvadeRange = 15f;

    [Header("Formation")]
    [Tooltip("Distance from slot under which the fighter stops actively steering toward the slot " +
             "and matches the squad heading instead.")]
    [SerializeField] private float formationTolerance = 3f;

    [Tooltip("Converts along-track slot error (units) into a speed offset from the squad speed " +
             "(units/s) while holding formation: behind the slot -> faster, ahead -> slower.")]
    [SerializeField] private float catchUpGain = 1.5f;

    [Header("Squad")]
    public Squad squad;
    public FighterOrder currentOrder;

    private List<WeaponGroup> groups;

    // Tick scheduling (absolute-time, so missed ticks are dropped after a slow frame
    // instead of all fighters piling onto the next frame in lock-step).
    private float nextTickTime;

    // Engage state (mirrors DogfightTargetAction)
    private float faceEnemyAngle = 1f;
    private float nextAdjust;

    private float lastSampledDistance;
    private float disengageBuildup;
    private float disengageSampleTimer;
    private float disengageThreshold = 2f;
    private float disengageTimeLimit;
    private float disengageTimeLimitMin = 0.25f;
    private float disengageTimeLimitMax = 1f;
    private float disengageSampleInterval = 0.5f;

    private bool isDisengaging;
    private float disengageTimer;
    private float disengageDuration = 0.75f;

    // Evade state (mirrors FleeFromMissileAction)
    private float jinkOffset;
    private float jinkTimer;
    private float flareRearAngle = 60f;
    private float flareEmergencyRange = 5f;

    // Solo target search (used when no squad has assigned an order yet).
    private float soloSearchTimer;
    private float soloSearchInterval = 0.5f;

    protected override void Start()
    {
        base.Start();
        groups = ship.GetWeaponGroups();
        nextTickTime = Time.time + Random.Range(0f, tickInterval);
        soloSearchTimer = Random.Range(0f, soloSearchInterval);
        ResetDisengageTracking();
        RollJink();
    }

    private void Update()
    {
        if (Time.time < nextTickTime) return;
        nextTickTime = Time.time + tickInterval;
        Tick();
    }

    private void Tick()
    {
        if (squad == null)
            UpdateSoloOrder();

        // Sync the AIControllerBase target with the order so missile seeker handoff and
        // damage attribution use the same target the squad assigned.
        if (currentOrder.target != null)
            SetTarget(currentOrder.target.gameObject);

        FighterMode effectiveMode = currentOrder.mode;
        if (HasCloseIncomingMissile(missileEvadeRange))
            effectiveMode = FighterMode.Evade;
        else if (effectiveMode == FighterMode.Engage && Time.time < currentOrder.orderTimestamp)
            // Staggered break: hold formation until our peel-off timestamp arrives.
            effectiveMode = FighterMode.Holdout;

        switch (effectiveMode)
        {
            case FighterMode.Engage:
                TickEngage();
                break;
            case FighterMode.Evade:
                TickEvade();
                break;
            case FighterMode.Holdout:
                TickHoldout();
                break;
            case FighterMode.Idle:
            default:
                break;
        }
    }

    // ===== Solo target search (replaces FindTargetAction until squads exist) =====

    private void UpdateSoloOrder()
    {
        soloSearchTimer -= tickInterval;
        bool needsTarget = target == null || !target.activeInHierarchy;
        if (!needsTarget && soloSearchTimer > 0f) return;

        soloSearchTimer = soloSearchInterval;

        List<ShipBase> enemies = SceneManager.Instance.GetLiveEnemies(tag);
        if (enemies.Count == 0)
        {
            currentOrder = default;
            currentOrder.mode = FighterMode.Idle;
            return;
        }

        Vector2 selfPos = rb.position;
        float bestDistSqr = float.PositiveInfinity;
        ShipBase best = null;
        for (int i = 0; i < enemies.Count; i++)
        {
            ShipBase e = enemies[i];
            if (!e.gameObject.activeInHierarchy) continue;

            float distSqr = (e.GetRigidBody().position - selfPos).sqrMagnitude;
            if (distSqr < bestDistSqr)
            {
                bestDistSqr = distSqr;
                best = e;
            }
        }

        if (best == null)
        {
            currentOrder = default;
            currentOrder.mode = FighterMode.Idle;
            return;
        }

        SetTarget(best.gameObject);
        currentOrder = default;
        currentOrder.mode = FighterMode.Engage;
        currentOrder.target = best;
        currentOrder.authorizeMissile = true;
    }

    // ===== Engage (port of DogfightTargetAction) =====

    private void TickEngage()
    {
        ShipBase t = currentOrder.target;
        if (t == null || !t.gameObject.activeInHierarchy)
            return;

        Rigidbody2D targetRb = t.GetRigidBody();
        float dt = tickInterval;
        if (nextAdjust > 0f) nextAdjust -= dt;

        float directAngle = ship.GetAngleToTarget(targetRb);
        float distance = (targetRb.position - rb.position).magnitude;
        float absAngle = Mathf.Abs(directAngle);

        if (isDisengaging)
        {
            BreakTurn(directAngle, distance, dt, absAngle, targetRb);
        }
        else
        {
            UpdateDisengageTimer(distance, dt);
            FollowTarget(directAngle);
            AttackTarget(absAngle, targetRb, allowMissileGate: true);
        }
    }

    private void BreakTurn(float angle, float distance, float dt, float absAngle, Rigidbody2D targetRb)
    {
        float reverseTurn = Mathf.Clamp(-angle / 45f, -1f, 1f);
        ship.SetShipTurn(reverseTurn);
        ship.Accelerate(0.2f);

        AttackTarget(absAngle, targetRb, allowMissileGate: true);

        disengageTimer -= dt;
        if (disengageTimer <= 0f)
        {
            isDisengaging = false;
            disengageBuildup = 0f;
            disengageSampleTimer = 0f;
            disengageTimeLimit = Random.Range(disengageTimeLimitMin, disengageTimeLimitMax);
            lastSampledDistance = distance;
        }
    }

    private void UpdateDisengageTimer(float distance, float dt)
    {
        disengageSampleTimer -= dt;
        if (disengageSampleTimer > 0f) return;

        if (lastSampledDistance > 0f)
        {
            if (Mathf.Abs(distance - lastSampledDistance) < disengageThreshold)
                disengageBuildup += disengageSampleInterval;
            else
                disengageBuildup = 0f;
        }

        lastSampledDistance = distance;
        disengageSampleTimer = disengageSampleInterval;

        if (disengageBuildup >= disengageTimeLimit)
        {
            isDisengaging = true;
            disengageTimer = disengageDuration;
        }
    }

    private void FollowTarget(float angle)
    {
        float turn = 0f;
        if (Mathf.Abs(angle) > faceEnemyAngle)
            turn = Mathf.Clamp(angle / 45f, -1f, 1f);

        if (nextAdjust <= 0f)
        {
            float absAngle = Mathf.Abs(angle);
            if (absAngle < 70f)
                ship.Accelerate(0.2f);
            else if (absAngle >= 90f)
                ship.Decelerate(0.2f);
            else
                nextAdjust = Random.Range(0f, 0.2f);
        }

        ship.SetShipTurn(turn);
    }

    private void ResetDisengageTracking()
    {
        lastSampledDistance = 0f;
        disengageBuildup = 0f;
        disengageSampleTimer = 0f;
        disengageTimeLimit = Random.Range(disengageTimeLimitMin, disengageTimeLimitMax);
        isDisengaging = false;
        disengageTimer = 0f;
    }

    // ===== Evade (port of FleeFromMissileAction) =====

    private void TickEvade()
    {
        float dt = tickInterval;

        jinkTimer -= dt;
        if (jinkTimer <= 0f) RollJink();

        EvadeMissiles(dt);

        ShipBase t = currentOrder.target;
        if (t != null && t.gameObject.activeInHierarchy)
            UrgentAttack(t.GetRigidBody());
    }

    private void EvadeMissiles(float dt)
    {
        Vector2 myPos = rb.position;
        Vector2 evadeDir = Vector2.zero;
        float closestDistSqr = float.MaxValue;
        List<WeaponsAAMissile> missiles = incomingMissiles;

        for (int i = missiles.Count - 1; i >= 0; i--)
        {
            WeaponsAAMissile missile = missiles[i];
            if (missile == null || !missile.gameObject.activeInHierarchy)
            {
                missiles.RemoveAt(i);
                continue;
            }

            Vector2 toShip = myPos - (Vector2)missile.transform.position;
            float distSqr = toShip.sqrMagnitude;
            float dist = Mathf.Sqrt(distSqr);
            if (dist < 0.01f) continue;

            if (distSqr < closestDistSqr)
                closestDistSqr = distSqr;

            float weight = 1f / dist;
            evadeDir += toShip.normalized * weight;
        }

        if (evadeDir.sqrMagnitude < 0.0001f)
            return;

        evadeDir.Normalize();

        float evadeAngle = Mathf.Atan2(evadeDir.y, evadeDir.x) * Mathf.Rad2Deg;
        float jinkedAngle = evadeAngle + jinkOffset;
        float jinkedRad = jinkedAngle * Mathf.Deg2Rad;
        Vector2 jinkedDir = new Vector2(Mathf.Cos(jinkedRad), Mathf.Sin(jinkedRad));

        float angle = Vector2.SignedAngle((Vector2)transform.up, jinkedDir);
        float turn = 0f;
        if (Mathf.Abs(angle) > 5f)
            turn = angle > 0 ? 1f : -1f;

        ship.Accelerate(0.2f);
        ship.SetShipTurn(turn);

        Vector2 rear = -(Vector2)transform.up;
        Vector2 threatDir = -evadeDir;
        float rearAngle = Vector2.Angle(rear, threatDir);
        bool rearFacingThreat = rearAngle <= flareRearAngle;
        bool missileEmergency = closestDistSqr <= flareEmergencyRange * flareEmergencyRange;

        if (rearFacingThreat || missileEmergency)
            ship.ActivateCombatSystem();
    }

    private void RollJink()
    {
        jinkOffset = Random.Range(-1f, 1f) * 60f;
        jinkTimer = Random.Range(0.3f, 0.8f);
    }

    private void UrgentAttack(Rigidbody2D targetRb)
    {
        for (int i = 0; i < groups.Count; i++)
        {
            WeaponGroup group = groups[i];
            if (group.autonomous) continue;

            WeaponsBase repWeapon = group.GetRepresentativeWeapon();
            if (repWeapon == null) continue;

            List<Hardpoint> hps = group.GetHardpoints();
            Vector2 avgPos = Vector2.zero;
            for (int j = 0; j < hps.Count; j++)
                avgPos += (Vector2)hps[j].transform.position;
            avgPos /= hps.Count;

            float speed = repWeapon.GetSpeed();
            float distance = Vector2.Distance(avgPos, targetRb.position);
            float travelTime = distance / speed;
            Vector2 aimPos = targetRb.position + targetRb.linearVelocity * travelTime;

            ship.FireGroup(i, aimPos);
        }
    }

    // ===== Holdout =====

    private void TickHoldout()
    {
        Vector2 myPos = rb.position;
        Vector2 forward = transform.up;

        // Velocity matching: aim slightly ahead of the slot so we don't lag a moving formation.
        Vector2 desiredPos = currentOrder.slotPosition + currentOrder.squadVelocity * tickInterval;
        Vector2 toSlot = desiredPos - myPos;
        float distToSlot = toSlot.magnitude;

        Vector2 desiredHeading;
        if (distToSlot < formationTolerance)
            desiredHeading = currentOrder.squadHeading.sqrMagnitude > 0.0001f
                ? currentOrder.squadHeading
                : forward;
        else
            desiredHeading = toSlot / distToSlot;

        float angle = Vector2.SignedAngle(forward, desiredHeading);
        float turn = Mathf.Abs(angle) > 1f ? Mathf.Clamp(angle / 45f, -1f, 1f) : 0f;
        ship.SetShipTurn(turn);

        // Throttle: match the squad's speed, corrected by the along-track slot error so we
        // speed up when trailing the slot and ease off when ahead of it.
        Vector2 alongAxis = currentOrder.squadHeading.sqrMagnitude > 0.0001f
            ? currentOrder.squadHeading
            : forward;
        float alongTrack = Vector2.Dot(toSlot, alongAxis);
        float desiredSpeed = Mathf.Clamp(
            currentOrder.squadVelocity.magnitude + alongTrack * catchUpGain,
            ship.GetShipMinSpeed(), ship.GetShipMaxSpeed());

        // Step at most 0.2 per tick toward the desired speed, and never past it — Accelerate is
        // all-or-nothing at the maxSpeed clamp, so an overshooting step would silently no-op.
        float speedDiff = desiredSpeed - ship.GetShipSpeed();
        if (speedDiff > 0.05f)
            ship.Accelerate(Mathf.Min(speedDiff, 0.2f));
        else if (speedDiff < -0.05f)
            ship.Decelerate(Mathf.Min(-speedDiff, 0.2f));

        // Opportunistic plasma fire while in formation but don't break heading for it.
        ShipBase t = currentOrder.target;
        if (t != null && t.gameObject.activeInHierarchy)
        {
            Rigidbody2D targetRb = t.GetRigidBody();
            float angleToTarget = Mathf.Abs(ship.GetAngleToTarget(targetRb));
            AttackTarget(angleToTarget, targetRb, allowMissileGate: currentOrder.authorizeMissile);
        }
    }

    // ===== Shared firing path =====

    private void AttackTarget(float angleToTarget, Rigidbody2D targetRb, bool allowMissileGate)
    {
        for (int i = 0; i < groups.Count; i++)
        {
            WeaponGroup group = groups[i];
            if (group.autonomous) continue;
            if (!group.HasReadyHardpoint()) continue;

            WeaponsBase repWeapon = group.GetRepresentativeWeapon();
            if (repWeapon == null) continue;

            List<Hardpoint> hps = group.GetHardpoints();
            float fireArc = hps[0].GetFireArc();
            if (fireArc < 360f && angleToTarget > fireArc)
                continue;

            bool isMissileGroup = (repWeapon.GetUsage() & WeaponUsage.Missile) != 0;
            if (isMissileGroup)
            {
                if (!allowMissileGate) continue;
                if (Time.time < currentOrder.missileFireTime) continue;
                if (angleToTarget > 45f) continue;
            }

            Vector2 avgPos = Vector2.zero;
            for (int j = 0; j < hps.Count; j++)
                avgPos += (Vector2)hps[j].transform.position;
            avgPos /= hps.Count;

            Vector2 aimPos = GetLeadPosition(repWeapon, avgPos, targetRb);
            ship.FireGroup(i, aimPos);
        }
    }

    private static Vector2 GetLeadPosition(WeaponsBase weapon, Vector2 gunPosition, Rigidbody2D targetRb)
    {
        float speed = weapon.GetSpeed();
        float distance = Vector2.Distance(gunPosition, targetRb.position);
        float travelTime = distance / speed;
        return targetRb.position + targetRb.linearVelocity * travelTime;
    }
}
