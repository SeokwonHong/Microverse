using UnityEngine;
using Unity.Mathematics;

namespace Microverse.Scripts.Simulation
{
    public class PlayerController : MonoBehaviour
    {
        [Header("Player")]
        public float radius = 0.2f;
        public float speed = 6f;
        public bool wrapEdges = false;

        [Header("World Ref")]
        public Vector2 worldSize = new Vector2(20, 11.25f);

        public float2 Pos { get; private set; }  // 월드 좌표(float2로 제공)

        void Start()
        {
            Pos = float2.zero;
        }

        void Update()
        {
            // 입력 (구 입력 시스템 기준)
            float2 mv = new float2(
                Input.GetAxisRaw("Horizontal"),
                Input.GetAxisRaw("Vertical")
            );
            if (math.lengthsq(mv) > 1e-6f) mv = math.normalize(mv);

            Pos += mv * speed * Time.deltaTime;

            // 경계 처리
            if (wrapEdges)
            {
                Pos = new float2(
                    Wrap01(Pos.x, worldSize.x),
                    Wrap01(Pos.y, worldSize.y)
                );
            }
            else
            {
                float hx = worldSize.x * 0.5f, hy = worldSize.y * 0.5f;
                Pos.x = math.clamp(Pos.x, -hx + radius, hx - radius);
                Pos.y = math.clamp(Pos.y, -hy + radius, hy - radius);
            }
        }

        static float Wrap01(float x, float range)
        {
            float half = range * 0.5f;
            if (x < -half) x += range;
            else if (x > half) x -= range;
            return x;
        }
    }
}
