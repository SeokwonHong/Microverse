using UnityEngine;
using Unity.Mathematics;

namespace Microverse.Scripts.Simulation.Runtime
{
    public class PlayerController : MonoBehaviour
    {
        [Header("Player Bacteria")]
        [Tooltip("플레이어 박테리아를 조작할지 여부 (CellSimulation에서 playerEnabled로 들어감)")]
        public bool enablePlayer = true;

        [Tooltip("플레이어 박테리아 반지름 (CellSimulation → SimParams.playerRadius 로 전달됨)")]
        public float radius = 0.25f;

        [Tooltip("플레이어 체력 (백혈구가 붙으면 CellSimulation에서 깎음)")]
        public float health = 100f;

        [Header("World Bounds (CellSimulation에서 셋업)")]
        [Tooltip("시뮬레이션 월드 크기 (CellSimulation.worldSize와 동일하게 세팅됨)")]
        public Vector2 worldSize = new Vector2(20, 11.25f);

        [Tooltip("가장자리를 넘어가면 반대편으로 워프할지 여부 (CellSimulation.wrapEdges와 동일)")]
        public bool wrapEdges = false;

        [Header("Movement")]
        [Tooltip("마우스 위치로 얼마나 빠르게 따라갈지. 값이 클수록 더 즉각적으로 붙음")]
        [Range(0.0f, 20f)]
        public float followSpeed = 10f;

        // ===== 시뮬레이션에서 직접 읽는 값들 =====
        // CellSimulation.Update()에서:
        // player.Pos, player.Vel, player.radius, player.enablePlayer 를 읽어서 SimParams에 넣음
        public float2 Pos => _pos;
        public float2 Vel => _vel;

        Vector2 _pos;
        Vector2 _prevPos;
        Vector2 _vel;

        Camera _cam;

        void Start()
        {
            _cam = Camera.main;

            _pos = transform.position;
            _prevPos = _pos;

            // quad mesh 기준: 시각적인 크기를 radius에 맞춤
            transform.localScale = new Vector3(radius * 2f, radius * 2f, 1f);
        }

        void Update()
        {
            if (!enablePlayer)
            {
                _vel = Vector2.zero;
                return;
            }

            if (_cam == null)
                _cam = Camera.main;
            if (_cam == null) return;

            // --- 마우스 위치 → 월드 좌표로 변환 ---
            Vector3 mouseScreen = Input.mousePosition;

            // 오쏘 카메라 기준: z는 카메라 평면까지 거리 사용
            float z = -_cam.transform.position.z;
            Vector3 mouseWorld3 = _cam.ScreenToWorldPoint(new Vector3(mouseScreen.x, mouseScreen.y, z));
            Vector2 target = new Vector2(mouseWorld3.x, mouseWorld3.y);

            // --- 월드 경계 처리 (CellSimulation.worldSize 기준) ---
            float halfX = worldSize.x * 0.5f;
            float halfY = worldSize.y * 0.5f;

            if (wrapEdges)
            {
                if (target.x < -halfX) target.x += worldSize.x;
                if (target.x > halfX) target.x -= worldSize.x;
                if (target.y < -halfY) target.y += worldSize.y;
                if (target.y > halfY) target.y -= worldSize.y;
            }
            else
            {
                target.x = Mathf.Clamp(target.x, -halfX + radius, halfX - radius);
                target.y = Mathf.Clamp(target.y, -halfY + radius, halfY - radius);
            }

            // --- 마우스 위치로 부드럽게 따라가기 (followSpeed로 보간 강도 조절) ---
            _prevPos = _pos;
            float t = Mathf.Clamp01(followSpeed * Time.deltaTime);
            _pos = Vector2.Lerp(_pos, target, t);

            // 실제 트랜스폼 위치 반영
            transform.position = new Vector3(_pos.x, _pos.y, 0f);

            // 속도 계산 (백혈구 shakeBreakSpeed 체크에 사용됨)
            _vel = (_pos - _prevPos) / Mathf.Max(Time.deltaTime, 1e-6f);
        }
    }
}
