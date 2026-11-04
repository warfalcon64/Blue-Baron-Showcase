using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WeaponsAAMissile : WeaponsBase
{
    [Header("Radar Seeker")]
    [SerializeField] private float seekerHalfAngle = 30f;
    [SerializeField] private float seekerSweepRate = 60f;
    [SerializeField] private float beamHalfWidth = 5f;
    [SerializeField] private float seekerRevisitRate = 15f;
    [SerializeField] private float lockHoldTime = 3f;
    [SerializeField] private float targetRadarSignature = 5f;
    [SerializeField] private LayerMask seekerLayerMask;

    [Header("One Turn")]
    [SerializeField] private float oneTurnDelay = 1f;
    [SerializeField] private float oneTurnAngleThreshold = 10f;
    [SerializeField] private float turnAcceleration = 50f;

    [Header("Proportional Navigation")]
    [SerializeField] private float navigationGain = 4f;
    [SerializeField] private float boostAcceleration = 30f;

    [Header("Fuel")]
    [SerializeField] private float totalFuel = 100f;
    [SerializeField] private float thrustFuelRate = 20f;
    [SerializeField] private float rcsFuelRate = 15f;
    
    [Header("Team Trail Colors")]
    [SerializeField] private Color blueTeamTrailStart = new Color(0.1f, 0.73f, 1f, 1f);
    [SerializeField] private Color blueTeamTrailEnd = new Color(0.35f, 0.68f, 0.82f, 1f);
    [SerializeField] private Color redTeamTrailStart = new Color(1f, 0.5f, 0.1f, 1f);
    [SerializeField] private Color redTeamTrailEnd = new Color(0.82f, 0.45f, 0.2f, 1f);

    private float previousLOSAngle;
    private bool hasPreviousLOS;
    private float currentSpeed;
    private float fuelRemaining;

    private bool oneTurnComplete;
    private float oneTurnTimer;
    private float currentTurnSpeed;
    private Vector2 missileVelocity;

    private GameObject previousTarget;

    private float seekerCurrentAngle;
    private bool seekerSweepRight = true;
    private float lockHoldTimer;
    private bool hasLock;
    private bool hadTargetLastFrame;

    private Vector2 lastKnownPosition;
    private Vector2 lastKnownVelocity;
    private float timeSinceLastContact;
    private float revisitTimer;

    private Rigidbody2D rb;
    private TrailRenderer engineTrail;

    private float expireTime;
    // Guards against double pool-release when several trigger contacts land in one physics step.
    private bool live;

    public override bool IsPooled => true;
    public override ObjectPoolManager.PoolType PoolType => ObjectPoolManager.PoolType.Missile;

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        engineTrail = GetComponentInChildren<TrailRenderer>();
    }

    private void FixedUpdate()
    {
        if (Time.time >= expireTime)
        {
            Despawn();
            return;
        }

        // A despawned pooled flare (or a dead, deactivated ship) never becomes null — release
        // the lock explicitly so a recycled flare can't drag the seeker to its new position.
        if (target != null && !target.activeInHierarchy)
        {
            target = null;
            hasLock = false;
        }

        float dt = Time.fixedDeltaTime;

        if (source == null && !oneTurnComplete)
        {
            oneTurnComplete = true;
            hasPreviousLOS = false;
        }

        if (fuelRemaining > 0f)
        {
            float fuelCost = Mathf.Min(thrustFuelRate * dt, fuelRemaining);

            currentSpeed = Mathf.MoveTowards(currentSpeed, speed, boostAcceleration * dt);

            fuelRemaining -= fuelCost;
        }

        if (fuelRemaining <= 0f && engineTrail != null && engineTrail.emitting)
        {
            engineTrail.emitting = false;
        }

        if (oneTurnComplete && fuelRemaining > 0f)
        {
            timeSinceLastContact += dt;

            // Lose lock if sweep hasn't re-contacted target within hold window
            if (hasLock && timeSinceLastContact > lockHoldTime)
            {
                // Debug.Log($"[Missile] Lock lost — no sweep contact for {lockHoldTime:F1}s");
                hasLock = false;
                hasPreviousLOS = false;
            }

            UpdateSeeker(dt);
        }

        // Guidance — only steer when locked
        if (target != null && (hasLock || !oneTurnComplete))
        {
            // During one-turn, use real target position; after, use extrapolated track
            Vector2 guidancePos;
            if (!oneTurnComplete)
                guidancePos = (Vector2)target.transform.position;
            else
                guidancePos = lastKnownPosition + lastKnownVelocity * timeSinceLastContact;

            Vector2 los = guidancePos - rb.position;

            // If extrapolated position is behind or on top of missile, coast instead
            if (oneTurnComplete && (los.sqrMagnitude < 1f || Vector2.Dot(los, missileVelocity) < 0f))
            {
                missileVelocity = missileVelocity.normalized * currentSpeed;
                hasPreviousLOS = false;
                hadTargetLastFrame = true;
            }
            else
            {
                float losAngle = Mathf.Atan2(los.y, los.x);

                if (!oneTurnComplete)
                    UpdateOneTurn(dt, los, losAngle);
                else if (hasPreviousLOS)
                    UpdateProNav(dt, los, losAngle);

                previousLOSAngle = losAngle;
                hasPreviousLOS = true;
                hadTargetLastFrame = true;
            }
        }
        else
        {
            // Coast ballistically — no target or lost lock
            if (hadTargetLastFrame)
            {
                // Debug.Log($"[Missile] Lost lock, coasting ballistically (fuel: {fuelRemaining:F1})");
                hadTargetLastFrame = false;
            }
            missileVelocity = missileVelocity.normalized * currentSpeed;
        }

        // Update rotation to face velocity direction
        if (missileVelocity.sqrMagnitude > 0.01f)
        {
            float angle = Mathf.Atan2(missileVelocity.y, missileVelocity.x) * Mathf.Rad2Deg - 90f;
            rb.rotation = angle;
        }

        rb.linearVelocity = missileVelocity;
    }

    public override void Setup(Vector2 shootDirection, Vector2 shipVelocity, ShipBase source)
    {
        this.source = source;
        gameObject.tag = source.tag;

        // Set trail color based on team
        if (engineTrail != null)
        {
            Gradient gradient = new Gradient();
            Color startColor = source.CompareTag("Blue") ? blueTeamTrailStart : redTeamTrailStart;
            Color endColor = source.CompareTag("Blue") ? blueTeamTrailEnd : redTeamTrailEnd;
            gradient.SetKeys(
                new GradientColorKey[] { new GradientColorKey(startColor, 0f), new GradientColorKey(endColor, 1f) },
                new GradientAlphaKey[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) }
            );
            engineTrail.colorGradient = gradient;
        }

        missileVelocity = shipVelocity;
        currentSpeed = missileVelocity.magnitude;
        fuelRemaining = totalFuel;
        hasPreviousLOS = false;
        oneTurnComplete = false;
        oneTurnTimer = 0f;
        currentTurnSpeed = 0f;
        hasLock = true;
        timeSinceLastContact = 0f;
        revisitTimer = 0f;

        // Reset state a recycled missile carries over from its previous flight.
        seekerCurrentAngle = 0f;
        seekerSweepRight = true;
        lockHoldTimer = 0f;
        hadTargetLastFrame = false;
        target = null;
        previousTarget = null;
        if (engineTrail != null)
        {
            // Clear() drops the trail points left at the previous death position, otherwise the
            // teleport from pool parking to this spawn draws a streak across the battlefield.
            engineTrail.Clear();
            engineTrail.emitting = true;
        }

        live = true;
        expireTime = Time.time + lifetime;
    }


    private void UpdateOneTurn(float dt, Vector2 los, float losAngle)
    {
        oneTurnTimer += dt;
        if (oneTurnTimer < oneTurnDelay)
        {
            missileVelocity = missileVelocity.normalized * currentSpeed;
            return;
        }

        // Check if one-turn phase is complete
        float velAngle = Mathf.Atan2(missileVelocity.y, missileVelocity.x) * Mathf.Rad2Deg;
        float losAngleDeg = losAngle * Mathf.Rad2Deg;
        float angleDiff = Mathf.Abs(Mathf.DeltaAngle(velAngle, losAngleDeg));

        if (angleDiff <= oneTurnAngleThreshold)
        {
            // Debug.Log($"[Missile] One-turn complete, switching to seeker/PN phase");
            oneTurnComplete = true;

            // Seed fresh track data for seeker handoff
            lastKnownPosition = (Vector2)target.transform.position;
            Rigidbody2D targetRb = target.GetComponent<Rigidbody2D>();
            lastKnownVelocity = targetRb != null ? targetRb.linearVelocity : Vector2.zero;
            timeSinceLastContact = 0f;
            revisitTimer = 0f;
            return;
        }

        // Direct pursuit, ramp up turn rate
        currentTurnSpeed = Mathf.MoveTowards(currentTurnSpeed, turnSpeed, turnAcceleration * dt);

        Vector2 velDir = missileVelocity.normalized;
        Vector2 losDir = los.normalized;
        Vector2 perpDir = new Vector2(-velDir.y, velDir.x);

        // Scale lateral acceleration by speed ratio so angular turn rate is consistent
        float turnDirection = Vector2.Dot(losDir, perpDir);
        float speedRatio = speed > 0f ? currentSpeed / speed : 0f;
        float clampedAccel = Mathf.Sign(turnDirection) * currentTurnSpeed * speedRatio;

        // RCS fuel cost
        float absAccel = Mathf.Abs(clampedAccel);
        float rcsCost = rcsFuelRate * absAccel * dt;
        if (rcsCost > fuelRemaining)
        {
            float scaleFactor = fuelRemaining / rcsCost;
            clampedAccel *= scaleFactor;
            rcsCost = fuelRemaining;
        }

        fuelRemaining -= rcsCost;

        missileVelocity += perpDir * clampedAccel * dt;
        missileVelocity = missileVelocity.normalized * currentSpeed;

        // Reset PN state so it starts fresh after one-turn completes
        hasPreviousLOS = false;
    }

    private void UpdateProNav(float dt, Vector2 los, float losAngle)
    {
        float losRate = Mathf.DeltaAngle(previousLOSAngle * Mathf.Rad2Deg, losAngle * Mathf.Rad2Deg) * Mathf.Deg2Rad / dt;

        // Closing speed using last known velocity (consistent with extrapolated guidance)
        Vector2 relVel = missileVelocity - lastKnownVelocity;
        Vector2 losDir = los.normalized;
        float closingSpeed = Mathf.Abs(Vector2.Dot(relVel, losDir));

        // PN commanded lateral acceleration: a = N * Vc * losRate
        float lateralAccelMag = navigationGain * closingSpeed * losRate;

        // Perpendicular to missile velocity (left-hand normal in 2D)
        Vector2 velDir = missileVelocity.normalized;
        Vector2 perpDir = new Vector2(-velDir.y, velDir.x);

        // Scale max lateral accel by speed ratio so angular turn rate stays constant
        float speedRatio = speed > 0f ? currentSpeed / speed : 0f;
        float maxAccel = turnSpeed * speedRatio;
        float clampedAccel = Mathf.Clamp(lateralAccelMag, -maxAccel, maxAccel);

        // RCS fuel cost
        float absAccel = Mathf.Abs(clampedAccel);
        float rcsCost = rcsFuelRate * absAccel * dt;
        if (rcsCost > fuelRemaining)
        {
            float scaleFactor = fuelRemaining / rcsCost;
            clampedAccel *= scaleFactor;
            absAccel *= scaleFactor;
            rcsCost = fuelRemaining;
        }

        fuelRemaining -= rcsCost;

        // Apply lateral acceleration to velocity
        missileVelocity += perpDir * clampedAccel * dt;

        // Re-normalize to currentSpeed (PN changes direction, not speed)
        missileVelocity = missileVelocity.normalized * currentSpeed;
    }

    private void UpdateSeeker(float dt)
    {
        if (lockHoldTimer > 0f)
            lockHoldTimer -= dt;

        if (hasLock && target != null)
            UpdateSeekerTrack(dt);
        else
            UpdateSeekerSearch(dt);
    }

    private void UpdateSeekerTrack(float dt)
    {
        revisitTimer -= dt;
        if (revisitTimer > 0f)
            return;

        revisitTimer = 1f / seekerRevisitRate;

        Vector2 velDir = missileVelocity.normalized;

        // Point beam at extrapolated target position
        Vector2 expectedPos = lastKnownPosition + lastKnownVelocity * timeSinceLastContact;
        Vector2 toExpected = expectedPos - rb.position;
        Vector2 lookDir = toExpected.normalized;

        float lookAngle = Mathf.Abs(Vector2.SignedAngle(velDir, lookDir));
        if (lookAngle > seekerHalfAngle)
            return;

        // Check if real target is within beam width of where we're looking
        Vector2 toTarget = (Vector2)target.transform.position - rb.position;
        float angleOffset = Vector2.Angle(lookDir, toTarget.normalized);

        if (angleOffset <= beamHalfWidth)
        {
            Rigidbody2D targetRb = target.GetComponent<Rigidbody2D>();
            lastKnownPosition = (Vector2)target.transform.position;
            lastKnownVelocity = targetRb != null ? targetRb.linearVelocity : Vector2.zero;
            timeSinceLastContact = 0f;
        }

        // Raycast toward expected position to check for chaff/flares
        float rayLength = toExpected.magnitude * 1.5f;
        RaycastHit2D hit = Physics2D.Raycast(rb.position, lookDir, rayLength, seekerLayerMask);
        if (hit.collider == null || hit.collider.gameObject == target)
            return;

        Flare flare = hit.collider.GetComponent<Flare>();
        if (flare == null)
            return;

        float detectedSignal = flare.GetChaffStrength();
        if (detectedSignal > targetRadarSignature && lockHoldTimer <= 0f)
            SwitchTarget(hit.collider.gameObject, flare, detectedSignal);
    }

    private void UpdateSeekerSearch(float dt)
    {
        float sweepDelta = seekerSweepRate * dt;
        if (seekerSweepRight)
        {
            seekerCurrentAngle += sweepDelta;
            if (seekerCurrentAngle >= seekerHalfAngle)
            {
                seekerCurrentAngle = seekerHalfAngle;
                seekerSweepRight = false;
            }
        }
        else
        {
            seekerCurrentAngle -= sweepDelta;
            if (seekerCurrentAngle <= -seekerHalfAngle)
            {
                seekerCurrentAngle = -seekerHalfAngle;
                seekerSweepRight = true;
            }
        }

        Vector2 velDir = missileVelocity.normalized;

        // Beam width check for current target — angular test for re-acquisition
        if (target != null)
        {
            Vector2 toTarget = (Vector2)target.transform.position - rb.position;
            float angleToTarget = Vector2.SignedAngle(velDir, toTarget.normalized);
            float angularOffset = Mathf.Abs(angleToTarget - seekerCurrentAngle);

            if (angularOffset <= beamHalfWidth)
            {
                // Debug.Log($"[Missile] Re-acquired lock on {target.name}");
                hasLock = true;
                hasPreviousLOS = false;

                Rigidbody2D targetRb = target.GetComponent<Rigidbody2D>();
                lastKnownPosition = (Vector2)target.transform.position;
                lastKnownVelocity = targetRb != null ? targetRb.linearVelocity : Vector2.zero;
                timeSinceLastContact = 0f;
                revisitTimer = 0f;
                return;
            }
        }

        // Raycast for discovering new contacts
        Vector2 rayDir = (Vector2)(Quaternion.Euler(0f, 0f, seekerCurrentAngle) * velDir);
        float rayLength = target != null
            ? Vector2.Distance(rb.position, (Vector2)target.transform.position)
            : speed * lifetime;

        RaycastHit2D hit = Physics2D.Raycast(rb.position, rayDir, rayLength, seekerLayerMask);
        if (hit.collider == null)
            return;

        if (target != null && hit.collider.gameObject == target)
            return;

        float detectedSignal;
        Flare flare = hit.collider.GetComponent<Flare>();
        if (flare != null)
            detectedSignal = flare.GetChaffStrength();
        else if (hit.collider.GetComponent<ShipBase>() != null)
            detectedSignal = targetRadarSignature;
        else
            return;

        float currentSignal = 0f;
        if (target != null)
        {
            Flare currentFlare = target.GetComponent<Flare>();
            if (currentFlare != null)
                currentSignal = currentFlare.GetChaffStrength();
            else if (target.GetComponent<ShipBase>() != null)
                currentSignal = targetRadarSignature;
        }

        if (detectedSignal > currentSignal && lockHoldTimer <= 0f)
            SwitchTarget(hit.collider.gameObject, flare, detectedSignal);
    }

    private void SwitchTarget(GameObject newTarget, Flare flare, float detectedSignal)
    {
        float currentSignal = 0f;
        if (target != null)
        {
            Flare currentFlare = target.GetComponent<Flare>();
            if (currentFlare != null)
                currentSignal = currentFlare.GetChaffStrength();
            else if (target.GetComponent<ShipBase>() != null)
                currentSignal = targetRadarSignature;
        }

        if (previousTarget != null)
        {
            AIControllerBase prevAI = previousTarget.GetComponent<AIControllerBase>();
            if (prevAI != null)
                prevAI.RemoveIncomingMissile(this);
        }

        bool wasFlare = flare != null;
        target = newTarget;
        previousTarget = target;
        hasLock = true;

        lastKnownPosition = (Vector2)target.transform.position;
        Rigidbody2D newTargetRb = target.GetComponent<Rigidbody2D>();
        lastKnownVelocity = newTargetRb != null ? newTargetRb.linearVelocity : Vector2.zero;
        timeSinceLastContact = 0f;
        revisitTimer = 0f;

        // if (wasFlare)
        //     Debug.Log($"[Missile] Locked onto flare (chaff strength: {detectedSignal:F1}) over previous target (signal: {currentSignal:F1})");
        // else
        //     Debug.Log($"[Missile] Locked onto ship {target.name} (signal: {detectedSignal:F1}) over previous target (signal: {currentSignal:F1})");

        if (target.TryGetComponent<AIControllerBase>(out AIControllerBase newAI))
            newAI.AddIncomingMissile(this);

        hasPreviousLOS = false;
        lockHoldTimer = 0.5f;
    }

    public override float GetSpeed()
    {
        return currentSpeed;
    }

    public override void SetTarget(GameObject newTarget)
    {
        if (previousTarget != null)
        {
            AIControllerBase prevAI = previousTarget.GetComponent<AIControllerBase>();
            if (prevAI != null)
                prevAI.RemoveIncomingMissile(this);
        }

        base.SetTarget(newTarget);
        previousTarget = newTarget;

        if (target != null)
        {
            AIControllerBase targetAI = target.GetComponent<AIControllerBase>();
            if (targetAI != null)
                targetAI.AddIncomingMissile(this);
        }
    }

    public GameObject GetTarget()
    {
        return target;
    }

    public float GetFuelFraction()
    {
        return totalFuel > 0f ? fuelRemaining / totalFuel : 0f;
    }

    private void OnDrawGizmos()
    {
        if (!Application.isPlaying || !oneTurnComplete)
            return;

        Vector2 velDir = missileVelocity.normalized;
        float rayLength = target != null
            ? Vector2.Distance(rb.position, (Vector2)target.transform.position)
            : speed * lifetime;

        // Draw cone edges
        Vector2 leftDir = (Vector2)(Quaternion.Euler(0f, 0f, seekerHalfAngle) * velDir);
        Vector2 rightDir = (Vector2)(Quaternion.Euler(0f, 0f, -seekerHalfAngle) * velDir);

        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(rb.position, rb.position + leftDir * rayLength);
        Gizmos.DrawLine(rb.position, rb.position + rightDir * rayLength);

        if (hasLock && target != null)
        {
            // Tracking mode — draw beam toward extrapolated position
            Vector2 expectedPos = lastKnownPosition + lastKnownVelocity * timeSinceLastContact;
            Vector2 lookDir = (expectedPos - rb.position).normalized;
            float lookDist = Vector2.Distance(rb.position, expectedPos);

            Vector2 lookPerp = new Vector2(-lookDir.y, lookDir.x);
            float beamEdgeOffset = Mathf.Tan(beamHalfWidth * Mathf.Deg2Rad) * lookDist;

            Gizmos.color = new Color(0f, 1f, 0f, 0.3f);
            Gizmos.DrawLine(rb.position, (Vector2)rb.position + lookDir * lookDist + lookPerp * beamEdgeOffset);
            Gizmos.DrawLine(rb.position, (Vector2)rb.position + lookDir * lookDist - lookPerp * beamEdgeOffset);

            Gizmos.color = Color.green;
            Gizmos.DrawLine(rb.position, (Vector2)rb.position + lookDir * lookDist);
        }
        else
        {
            // Search mode — draw sweep ray with beam width
            Vector2 sweepDir = (Vector2)(Quaternion.Euler(0f, 0f, seekerCurrentAngle) * velDir);
            Vector2 beamLeft = (Vector2)(Quaternion.Euler(0f, 0f, seekerCurrentAngle + beamHalfWidth) * velDir);
            Vector2 beamRight = (Vector2)(Quaternion.Euler(0f, 0f, seekerCurrentAngle - beamHalfWidth) * velDir);

            Gizmos.color = new Color(1f, 0f, 0f, 0.3f);
            Gizmos.DrawLine(rb.position, rb.position + beamLeft * rayLength);
            Gizmos.DrawLine(rb.position, rb.position + beamRight * rayLength);

            Gizmos.color = Color.red;
            Gizmos.DrawLine(rb.position, rb.position + sweepDir * rayLength);
        }
    }

    // Return to the pool. Drops this missile from its target's incoming list immediately, so AI
    // never sees a recycled missile as still inbound on itself.
    private void Despawn()
    {
        if (!live) return;
        live = false;

        ReleaseIncomingTracking();
        target = null;
        previousTarget = null;
        rb.linearVelocity = Vector2.zero;

        ObjectPoolManager.ReturnObjectToPool(gameObject, PoolType);
    }

    private void ReleaseIncomingTracking()
    {
        if (previousTarget != null)
        {
            AIControllerBase prevAI = previousTarget.GetComponent<AIControllerBase>();
            if (prevAI != null)
                prevAI.RemoveIncomingMissile(this);
        }
    }

    private void OnDestroy()
    {
        // Scene teardown / editor stop; pooled despawn already nulled previousTarget.
        ReleaseIncomingTracking();
    }

    private void OnTriggerEnter2D(Collider2D collision)
    {
        if (!live) return;

        if (collision.GetComponent<Rigidbody2D>() != null && !collision.CompareTag(tag))
        {
            SpawnImpactVFX();
            Despawn();
        }
    }
}
