using UnityEngine;

public struct FighterOrder
{
    public FighterMode mode;
    public Vector2 slotPosition;
    public Vector2 squadVelocity;
    public Vector2 squadHeading;
    public ShipBase target;
    public bool authorizeMissile;
    public float orderTimestamp;
    // Earliest Time.time at which this fighter may launch a missile-flagged weapon group.
    // Used by the squad to phase-stagger members' first volley against point defense.
    public float missileFireTime;
}
