using UnityEngine;

// Contract for projectiles driven by ProjectileManager's single per-frame loop instead of their
// own Update/FixedUpdate. The manager knows nothing about weapons or GameObjects.
public interface IManagedProjectile
{
    // Slot index owned by the manager.
    int ManagerIndex { get; set; }

    // Time accumulated by the manager between low-rate (off-screen) steps. Owned by the manager;
    // projectiles never touch it.
    float LodAccumulator { get; set; }

    // Current world position, used for the manager's viewport LOD test. Implementations should
    // return a cached value rather than reading transform.position (skipped bolts are the common
    // case, and this is read for every projectile every frame).
    Vector2 Position { get; }

    // Advance by deltaTime (one frame at full rate, several frames' worth at low rate).
    // Return false when finished (hit or expired).
    bool Step(float deltaTime);

    void Despawn();
}
