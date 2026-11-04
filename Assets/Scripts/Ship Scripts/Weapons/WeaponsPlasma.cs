using UnityEngine;

public class WeaponsPlasma : ManagedProjectile
{
    [Header("Non-physical hit detection")]
    [Tooltip("Layers tested for hits (ships). If left empty, defaults to the \"Ships\" layer at runtime.")]
    [SerializeField] private LayerMask hitLayers;
    [Tooltip("Cast radius approximating the bolt's half-width (prefab capsule was ~0.08 wide).")]
    [SerializeField] private float castRadius = 0.08f;

#if UNITY_EDITOR
    // Scene-view verification for the viewport LOD: tint bolts by the step size they received
    // (white = full rate, cyan = low rate). Toggled via Tools/Plasma LOD; watch in the Scene view
    // while the Game view camera defines the LOD bounds. Editor-only — builds never tint.
    public static bool TintByLodTier = false;

    [Header("Debug Gizmos (editor only)")]
    [Tooltip("Draw this step's swept CircleCast capsule in the Scene view (green = clear, red = hit).")]
    [SerializeField] private bool drawCastGizmo = false;
    [Tooltip("Also draw the capsule this bolt WOULD sweep if stepped at this rate (Hz). Set ~10 to see the long, gap-free corridor an off-screen (low-rate) bolt covers. 0 = off.")]
    [SerializeField] private float previewSweepHz = 0f;

    // Last cast parameters, recorded in Step for the gizmo to draw.
    private Vector2 gizOrigin, gizDir, gizHitPoint;
    private float gizDist;
    private bool gizHit;
    private SpriteRenderer tintRenderer;
#endif

    private Vector2 velocity;
    private Vector2 position;
    private float expireTime;

    public override ObjectPoolManager.PoolType PoolType => ObjectPoolManager.PoolType.Plasma;

    // Cached so the manager's per-frame LOD test never touches the transform of a skipped bolt.
    public override Vector2 Position => position;

    private void Awake()
    {
        if (hitLayers == 0)
            hitLayers = LayerMask.GetMask("Ships");
#if UNITY_EDITOR
        tintRenderer = GetComponentInChildren<SpriteRenderer>();
#endif
    }

    public override void Setup(Vector2 shootDirection, Vector2 shipVelocity, ShipBase source)
    {
        shootDirection = shootDirection.normalized;
        velocity = (shootDirection * speed) + shipVelocity;
        position = transform.position;
        this.source = source;
        expireTime = Time.time + lifetime;
#if UNITY_EDITOR
        if (!TintByLodTier && tintRenderer != null)
            tintRenderer.color = Color.white;
#endif

        EnterManager();
    }

    public override bool Step(float deltaTime)
    {
        if (Time.time >= expireTime)
            return false;

#if UNITY_EDITOR
        if (TintByLodTier && tintRenderer != null)
            tintRenderer.color = deltaTime >= 0.05f ? Color.cyan : Color.white;
#endif

        Vector2 pos = position;
        Vector2 step = velocity * deltaTime;
        float dist = step.magnitude;

        if (dist > 0f)
        {
            Vector2 dir = step / dist;
            // Single nearest hit along the travel segment; struct return, no allocation.
            RaycastHit2D hit = Physics2D.CircleCast(pos, castRadius, dir, dist, hitLayers);
#if UNITY_EDITOR
            if (drawCastGizmo) { gizOrigin = pos; gizDir = dir; gizDist = dist; gizHit = false; }
#endif
            Rigidbody2D hitRb = hit.rigidbody;
            if (hitRb != null)
            {
                ShipBase ship = hitRb.GetComponent<ShipBase>();
                if (ship != null && !ship.CompareTag(tag))
                {
#if UNITY_EDITOR
                    if (drawCastGizmo) { gizHit = true; gizHitPoint = hit.point; }
#endif
                    ship.ApplyWeaponDamage(this);
                    SpawnImpactVFX(velocity);
                    return false;
                }
            }
        }

        position = pos + step;
        transform.position = position;
        return true;
    }

    public override float GetSpeed() => base.GetSpeed();

    public override float GetDamage() => base.GetDamage();

    public override float GetRange() => base.GetRange();

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!drawCastGizmo) return;

        // The actual swept capsule tested this step. It is intentionally tiny at
        // high framerate, since length = speed * dt. Green = clear, red = hit.
        if (gizDist > 0f)
        {
            Gizmos.color = gizHit ? Color.red : Color.green;
            DrawWireCapsule2D(gizOrigin, gizOrigin + gizDir * gizDist, castRadius);
            if (gizHit)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawSphere(gizHitPoint, castRadius * 0.5f);
            }
        }

        // The capsule this bolt WOULD sweep at previewSweepHz. Set ~10 to see the
        // long, seam-free corridor an off-screen (low-rate) bolt covers in one step.
        if (previewSweepHz > 0f)
        {
            Vector2 vel = velocity;
            float speedNow = vel.magnitude;
            if (speedNow > 1e-4f)
            {
                Vector2 pos = transform.position;
                Vector2 dir = vel / speedNow;
                float previewDist = speedNow / previewSweepHz;
                Gizmos.color = new Color(0.2f, 0.7f, 1f);
                DrawWireCapsule2D(pos, pos + dir * previewDist, castRadius);
            }
        }
    }

    // Outline of a swept circle (stadium/capsule): two flat end circles joined by
    // parallel edges offset by the radius. Drawn in the XY plane for the 2D view.
    private static void DrawWireCapsule2D(Vector2 a, Vector2 b, float r)
    {
        Vector2 axis = b - a;
        float len = axis.magnitude;
        Vector2 dir = len > 1e-5f ? axis / len : Vector2.right;
        Vector2 n = new Vector2(-dir.y, dir.x) * r; // perpendicular, scaled to radius
        Gizmos.DrawLine(a + n, b + n);
        Gizmos.DrawLine(a - n, b - n);
        DrawCircleXY(a, r);
        DrawCircleXY(b, r);
    }

    private static void DrawCircleXY(Vector2 c, float r, int segments = 24)
    {
        Vector3 prev = new Vector3(c.x + r, c.y, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float ang = (i / (float)segments) * Mathf.PI * 2f;
            Vector3 cur = new Vector3(c.x + Mathf.Cos(ang) * r, c.y + Mathf.Sin(ang) * r, 0f);
            Gizmos.DrawLine(prev, cur);
            prev = cur;
        }
    }
#endif
}
