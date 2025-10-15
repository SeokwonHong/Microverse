using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Microverse.Scripts.Simulation
{
    [RequireComponent(typeof(Renderer))]
    public class CellManager512 : MonoBehaviour
    {
        [Header("Grid")]
        public int width = 512;
        public int height = 512;
        public bool wrapEdges = true;   // 경계 래핑(토러스)

        [Header("Sim")]
        [Range(0.01f, 0.2f)] public float tickSeconds = 0.03f; // 약 33Hz
        public int seed = 1;            // 랜덤 시드
        public bool pause = false;      // Space로 토글

        // 텍스처 & 타이머
        private Texture2D tex;
        private float timer;

        // 더블버퍼(상태: 0=빈, 1=A, 2=B)
        private NativeArray<byte> stateCur;
        private NativeArray<byte> stateNext;

        // 렌더 버퍼
        private NativeArray<Color32> pixelNA;

        // 팔레트
        static readonly Color32 colBg = new Color32(10, 12, 20, 255);
        static readonly Color32 colA = new Color32(60, 220, 160, 255);
        static readonly Color32 colB = new Color32(240, 90, 90, 255);

        void Awake()
        {
            Application.targetFrameRate = 120;

            // 1) 카메라 비율에 그리드 가로폭을 맞춘다 (정사각 방지)
            var cam = Camera.main;
            cam.orthographic = true;
            int targetWidth = Mathf.Max(1, Mathf.RoundToInt(height * cam.aspect)); // 예: 16:9면 512*1.777=~910
            width = targetWidth;

            // 2) 이제 맞춘 width/height로 버퍼/텍스처 생성
            int N = width * height;
            stateCur = new NativeArray<byte>(N, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            stateNext = new NativeArray<byte>(N, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            pixelNA = new NativeArray<Color32>(N, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            tex = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            tex.filterMode = FilterMode.Bilinear;

            var r = GetComponent<Renderer>();
            if (r.sharedMaterial == null) r.material = new Material(Shader.Find("Unlit/Texture"));
            r.sharedMaterial.mainTexture = tex;

            Randomize(0.55f);
            DrawImmediate();

            // 3) 정사각 스케일러 대신, 카메라 뷰에 꽉 차도록 세팅
            FitQuadToCameraView();
        }

        void OnDestroy()
        {
            if (stateCur.IsCreated) stateCur.Dispose();
            if (stateNext.IsCreated) stateNext.Dispose();
            if (pixelNA.IsCreated) pixelNA.Dispose();
        }

        void Update()
        {
            // 입력
            if (Input.GetKeyDown(KeyCode.Space)) pause = !pause;
            if (Input.GetKeyDown(KeyCode.R)) { Randomize(0.55f); DrawImmediate(); }
            if (Input.GetKeyDown(KeyCode.C)) { Clear(); DrawImmediate(); }
            HandleBrush();

            if (pause) return;

            timer += Time.deltaTime;
            if (timer < tickSeconds) return;
            timer = 0f;

            // 1) 시뮬 스텝(Job)
            var stepJob = new StepJob
            {
                W = width,
                H = height,
                Wrap = wrapEdges ? (byte)1 : (byte)0,
                StateCur = stateCur,
                StateNext = stateNext
            };
            JobHandle h1 = stepJob.Schedule(stateCur.Length, 256);
            h1.Complete();

            // 2) 버퍼 스왑
            (stateCur, stateNext) = (stateNext, stateCur);

            // 3) 픽셀 변환(Job)
            var drawJob = new MapToPixelsJob
            {
                State = stateCur,
                Pixels = pixelNA,
                ColBg = colBg,
                ColA = colA,
                ColB = colB
            };
            JobHandle h2 = drawJob.Schedule(pixelNA.Length, 512);
            h2.Complete();

            // 4) 텍스처 업로드(복사 없이)
            tex.SetPixelData(pixelNA, 0);
            tex.Apply(false, false);
        }

        // ===== 유틸 =====
        void Randomize(float density)
        {
            var rng = new System.Random(seed);
            for (int i = 0; i < stateCur.Length; i++)
            {
                double r = rng.NextDouble();
                stateCur[i] = (byte)(r < density ? (rng.NextDouble() < 0.5 ? 1 : 2) : 0);
            }
        }

        void Clear()
        {
            // MemClear 대신 안전한 루프 방식
            for (int i = 0; i < stateCur.Length; i++)
                stateCur[i] = 0;
        }
        void FitQuadToCameraView()
        {
            var cam = Camera.main;
            cam.orthographic = true;

            // 카메라가 보여주는 월드 높이/폭
            float worldHeight = cam.orthographicSize * 2f;
            float worldWidth = worldHeight * cam.aspect;

            // Quad를 카메라 뷰에 정확히 맞춤 (가로·세로 서로 다르게 스케일)
            transform.position = new Vector3(cam.transform.position.x, cam.transform.position.y, 0f);
            transform.rotation = Quaternion.identity;
            transform.localScale = new Vector3(worldWidth, worldHeight, 1f);
        }
        void HandleBrush()
        {
            // 좌클릭=A, 우클릭=B, 휠클릭=지우개(0)
            if (!Input.GetMouseButton(0) && !Input.GetMouseButton(1) && !Input.GetMouseButton(2)) return;

            var ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            if (!Physics.Raycast(ray, out var hit)) return;   // Quad에 Collider 필요(MeshCollider 등)

            var uv = hit.textureCoord;
            int cx = math.clamp((int)(uv.x * width), 0, width - 1);
            int cy = math.clamp((int)(uv.y * height), 0, height - 1);
            int r = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ? 3 : 1;

            byte s = 0;
            if (Input.GetMouseButton(0)) s = 1;
            else if (Input.GetMouseButton(1)) s = 2;
            else s = 0;

            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (wrapEdges)
                    {
                        if (x < 0) x += width; else if (x >= width) x -= width;
                        if (y < 0) y += height; else if (y >= height) y -= height;
                    }
                    else
                    {
                        if ((uint)x >= (uint)width || (uint)y >= (uint)height) continue;
                    }
                    stateCur[y * width + x] = s;
                }
        }

        
        void DrawImmediate()
        {
            for (int i = 0; i < pixelNA.Length; i++)
            {
                byte s = stateCur[i];
                pixelNA[i] = (s == 0) ? colBg : (s == 1 ? colA : colB);
            }
            tex.SetPixelData(pixelNA, 0);
            tex.Apply(false, false);
        }

        // ===== Burst Jobs =====
        [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
        struct StepJob : IJobParallelFor
        {
            public int W, H;
            public byte Wrap;

            [ReadOnly] public NativeArray<byte> StateCur;
            [WriteOnly] public NativeArray<byte> StateNext;

            public void Execute(int i)
            {
                int x = i % W;
                int y = i / W;

                byte s = StateCur[i];
                int nA = 0, nB = 0;

                // 8-이웃 카운트 (로컬 함수 대신 메서드 호출)
                CountAt(ref nA, ref nB, x - 1, y + 1);
                CountAt(ref nA, ref nB, x, y + 1);
                CountAt(ref nA, ref nB, x + 1, y + 1);
                CountAt(ref nA, ref nB, x - 1, y);
                CountAt(ref nA, ref nB, x + 1, y);
                CountAt(ref nA, ref nB, x - 1, y - 1);
                CountAt(ref nA, ref nB, x, y - 1);
                CountAt(ref nA, ref nB, x + 1, y - 1);

                // 간단 경쟁 룰
                byte nextS;
                if (s == 0)
                    nextS = (byte)((nA >= 3 && nA > nB) ? 1 : ((nB >= 3 && nB > nA) ? 2 : 0));
                else if (s == 1)
                    nextS = (byte)((nB >= nA + 2) ? 2 : ((nA < 2) ? 0 : 1));
                else
                    nextS = (byte)((nA >= nB + 2) ? 1 : ((nB < 2) ? 0 : 2));

                StateNext[i] = nextS;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void CountAt(ref int a, ref int b, int nx, int ny)
            {
                if (Wrap == 1)
                {
                    if (nx < 0) nx += W; else if (nx >= W) nx -= W;
                    if (ny < 0) ny += H; else if (ny >= H) ny -= H;
                }
                else
                {
                    if ((uint)nx >= (uint)W || (uint)ny >= (uint)H) return;
                }

                byte ns = StateCur[ny * W + nx];
                a += (ns == 1) ? 1 : 0;
                b += (ns == 2) ? 1 : 0;
            }
        }

        [BurstCompile]
        struct MapToPixelsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> State;
            [WriteOnly] public NativeArray<Color32> Pixels;

            public Color32 ColBg, ColA, ColB;

            public void Execute(int i)
            {
                byte s = State[i];
                Pixels[i] = (s == 0) ? ColBg : (s == 1 ? ColA : ColB);
            }
        }
    }
}
