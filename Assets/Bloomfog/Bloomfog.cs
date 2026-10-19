using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class Bloomfog : ScriptableRendererFeature
{
    //These are static fields for graphics settings to access
    public static bool Enabled = true;
    public static int Quality = 2;

    [System.Serializable]
    public class BloomfogSettings
    {
        public Material prepassMaterial;

        [Space]
        public float bloomCaptureExtraFov = 0f;
        public float threshold = 1f;
        public float brightnessMult = 1f;

        [Header("Blur Settings")]
        public Material blurMaterial;

        [Header("Output Settings")]
        public string outputTextureName;

        [Space]
        public BloomfogQualityPreset[] qualityPresets;

        [System.NonSerialized] public int textureWidth;
        [System.NonSerialized] public int textureHeight;
        [System.NonSerialized] public int actualDownsamplePasses;

        public BloomfogQualityPreset currentQualityPreset => qualityPresets[Mathf.Clamp(Quality, 0, qualityPresets.Length - 1)];
    }

    [System.Serializable]
    public class BloomfogQualityPreset
    {
        public int referenceScreenHeight = 1024;
        [Min(2)] public int downsamplePasses = 5;
        [Min(0f)] public float upsampleBlend = 20f;
        [Min(0)] public int ignoreUpsampleIndex = 1;
    }

    [SerializeField] private BloomfogSettings settings = new BloomfogSettings();

    // Only this camera (mono, render texture) produces the Bloomfog. The XR main camera
    // is NOT touched by the feature (otherwise its FOV would be overwritten).
    public static Camera CaptureCamera;

    private BloomFogPass bloomFogPass;


    public override void Create()
    {
        bloomFogPass?.Dispose();
        bloomFogPass = new BloomFogPass(settings);
        bloomFogPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }

    protected override void Dispose(bool disposing)
    {
        bloomFogPass?.Dispose();
        bloomFogPass = null;
        base.Dispose(disposing);
    }


    public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
    {
        Camera mainCamera = Camera.main;
        Camera renderCamera = cameraData.camera;
        if (CaptureCamera == null || renderCamera != CaptureCamera || mainCamera == null) return;

        BloomfogQualityPreset qualitySettings = settings.currentQualityPreset;

        //Update the camera field of view
        renderCamera.fieldOfView = Mathf.Clamp(mainCamera.fieldOfView + settings.bloomCaptureExtraFov, 30, 160);
        renderCamera.allowMSAA = false;

        float verticalFov = Mathf.Deg2Rad * renderCamera.fieldOfView;
        float horizontalFov = 2 * Mathf.Atan(Mathf.Tan(verticalFov / 2) * renderCamera.aspect);

        //Calculate the new texture ratio based on camera fov
        float originalVertFov = Mathf.Deg2Rad * mainCamera.fieldOfView;
        float screenPlaneDistance = qualitySettings.referenceScreenHeight / 2 / Mathf.Tan(originalVertFov / 2);

        //Set the new texture size
        float textureWidth = Mathf.Tan(horizontalFov / 2) * screenPlaneDistance * 2;
        float textureHeight = Mathf.Tan(verticalFov / 2) * screenPlaneDistance * 2;

        float referenceWidth = qualitySettings.referenceScreenHeight * mainCamera.aspect;
        float widthRatio = referenceWidth / textureWidth;
        float heightRatio = (float)qualitySettings.referenceScreenHeight / textureHeight;

        // Debug.Log($"fov: {verticalFov} horizontal: {horizontalFov} width: {settings.textureWidth} height: {settings.textureHeight} ratio: {widthRatio}, {heightRatio}");

        settings.textureHeight = qualitySettings.referenceScreenHeight;
        settings.textureWidth = (int)referenceWidth;

        Shader.SetGlobalVector("_FogTextureToScreenRatio", new Vector2(widthRatio, heightRatio));
    }


    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings.blurMaterial && settings.prepassMaterial
            && CaptureCamera != null && renderingData.cameraData.camera == CaptureCamera)
        {
            renderer.EnqueuePass(bloomFogPass);
        }
    }


    // ==================================================================================
    // RenderGraph pass (URP 17). Replaces the former Configure/Execute command-buffer path,
    // which was removed in URP 17. The flow is unchanged:
    //   Source --(prepass: threshold/brightness/fade)--> level 0
    //   Level i-1 --(box blur, alpha=1)--> level i                  (downsample)
    //   Level i   --(box blur, alpha=falloff)--> level i-1          (upsample, blends up)
    // Level 0 is published as a global shader texture slot (`outputTextureName`).
    // ==================================================================================
    private class BloomFogPass : ScriptableRenderPass
    {
        private const int MaxPasses = 16;

        private static readonly int ThresholdID = Shader.PropertyToID("_Threshold");
        private static readonly int BrightnessMultID = Shader.PropertyToID("_BrightnessMult");
        private static readonly int BlurAlphaID = Shader.PropertyToID("_BlurAlpha");

        private readonly BloomfogSettings settings;

        // Materials are references: all blits using the SAME material would see the
        // most recently set value at execution time. Hence a separate instance per blur level.
        private Material _prepassInstance;
        private Material _downsampleInstance;
        private readonly Material[] _upsampleInstances = new Material[MaxPasses];

        // Persistent output buffer: `_BloomfogTex` is read by scene materials of the
        // XR main camera and must therefore outlive the capture camera's render graph
        // (transient graph textures are released afterwards).
        private RTHandle _outputHandle;

        private TextureHandle[] _tempHandles = new TextureHandle[MaxPasses];


        public BloomFogPass(BloomfogSettings fogSettings)
        {
            settings = fogSettings;
        }

        public void Dispose()
        {
            CoreUtils.Destroy(_prepassInstance);
            CoreUtils.Destroy(_downsampleInstance);
            for (int i = 0; i < _upsampleInstances.Length; i++)
                CoreUtils.Destroy(_upsampleInstances[i]);
            _prepassInstance = null;
            _downsampleInstance = null;

            _outputHandle?.Release();
            _outputHandle = null;
        }


        private void EnsureMaterials(int passCount)
        {
            if (_prepassInstance == null && settings.prepassMaterial != null)
            {
                _prepassInstance = new Material(settings.prepassMaterial) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (_downsampleInstance == null && settings.blurMaterial != null)
            {
                _downsampleInstance = new Material(settings.blurMaterial) { hideFlags = HideFlags.HideAndDontSave };
                // Downsample: fully replace (alpha = 1), soft 25-tap cloud.
                _downsampleInstance.SetFloat(BlurAlphaID, 1f);
            }
            if (_prepassInstance != null)
            {
                _prepassInstance.SetFloat(ThresholdID, settings.threshold);
                _prepassInstance.SetFloat(BrightnessMultID, settings.brightnessMult);
            }

            BloomfogQualityPreset q = settings.currentQualityPreset;
            float blendDivisor = Mathf.Max(0.0001f, q.upsampleBlend);
            for (int i = 1; i < passCount; i++)
            {
                if (_upsampleInstances[i] == null && settings.blurMaterial != null)
                {
                    _upsampleInstances[i] = new Material(settings.blurMaterial) { hideFlags = HideFlags.HideAndDontSave };
                }
                if (_upsampleInstances[i] != null)
                {
                    // Blends the lower level onto the higher one with alpha falloff.
                    // High resolutions (i <= ignoreUpsampleIndex) stay unchanged so that no
                    // unblurred details are blended back in.
                    float alpha = i <= q.ignoreUpsampleIndex ? 1f : Mathf.Pow(0.5f, i / blendDivisor);
                    _upsampleInstances[i].SetFloat(BlurAlphaID, alpha);
                }
            }
        }


        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!Enabled)
            {
                if (!string.IsNullOrEmpty(settings.outputTextureName))
                {
                    //Bloomfog shouldn't be used, just output a black texture
                    Shader.SetGlobalTexture(settings.outputTextureName, Texture2D.blackTexture);
                }
                return;
            }

            if (settings.prepassMaterial == null || settings.blurMaterial == null)
                return;

            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();

            //Safety net: the feature only runs for the capture camera (gate in AddRenderPasses).
            if (CaptureCamera == null || cameraData.camera != CaptureCamera)
                return;

            TextureHandle source = resourceData.cameraColor;
            if (!source.IsValid())
                return;

            BloomfogQualityPreset qualitySettings = settings.currentQualityPreset;

            int width = Mathf.Max(2, settings.textureWidth);
            int height = Mathf.Max(2, settings.textureHeight);

            //Clamp the blur passes so we don't downsample below a 2x2 texture
            int minDimension = Mathf.Min(width, height);
            int maxDownsample = Mathf.FloorToInt(Mathf.Log(minDimension, 2f));
            int passCount = Mathf.Clamp(qualitySettings.downsamplePasses, 2, Mathf.Max(2, maxDownsample));
            settings.actualDownsamplePasses = passCount;

            EnsureMaterials(passCount);

            // Level 0 (output) is persistent.
            var outputDesc = new RenderTextureDescriptor(width, height, RenderTextureFormat.DefaultHDR, 0)
            {
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false
            };
            RenderingUtils.ReAllocateHandleIfNeeded(ref _outputHandle, outputDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "BloomfogOutput");
            TextureHandle output = renderGraph.ImportTexture(_outputHandle);
            _tempHandles[0] = output;

            // Smaller intermediate levels (transient, only within this graph).
            const GraphicsFormat hdrFormat = GraphicsFormat.R16G16B16A16_SFloat;
            for (int i = 1; i < passCount; i++)
            {
                int downsample = 1 << i;
                var tempDesc = new TextureDesc(Mathf.Max(1, width / downsample), Mathf.Max(1, height / downsample))
                {
                    colorFormat = hdrFormat,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    name = "BloomfogTemp" + i,
                    clearBuffer = false
                };
                _tempHandles[i] = renderGraph.CreateTexture(tempDesc);
            }

            var passData = new PassData
            {
                source = source,
                temps = _tempHandles,
                passCount = passCount,
                prepassMaterial = _prepassInstance,
                downsampleMaterial = _downsampleInstance,
                upsampleMaterials = _upsampleInstances
            };

            using (var builder = renderGraph.AddUnsafePass<PassData>("Bloomfog", out var data))
            {
                data.source = passData.source;
                data.temps = passData.temps;
                data.passCount = passData.passCount;
                data.prepassMaterial = passData.prepassMaterial;
                data.downsampleMaterial = passData.downsampleMaterial;
                data.upsampleMaterials = passData.upsampleMaterials;

                builder.UseTexture(source, AccessFlags.Read);
                for (int i = 1; i < passCount; i++)
                    builder.UseTexture(_tempHandles[i], AccessFlags.ReadWrite);
                builder.UseTexture(output, AccessFlags.Write);

                if (!string.IsNullOrEmpty(settings.outputTextureName))
                    builder.SetGlobalTextureAfterPass(output, Shader.PropertyToID(settings.outputTextureName));

                builder.SetRenderFunc(static (PassData d, UnsafeGraphContext context) => Execute(d, context));
            }
        }


        private static void Execute(PassData data, UnsafeGraphContext context)
        {
            var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            // 1) Mirror the source into level 0, with threshold/brightness/fade (prepass material).
            Blitter.BlitCameraTexture(cmd, data.source, data.temps[0],
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, data.prepassMaterial, 0);

            // 2) Blit into progressively smaller textures (soft box blur).
            for (int i = 1; i < data.passCount; i++)
            {
                Blitter.BlitCameraTexture(cmd, data.temps[i - 1], data.temps[i],
                    RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, data.downsampleMaterial, 0);
            }

            // 3) Blend the chain back up. Alpha < 1 blends the lower level onto the
            //    higher one (Load!), for a soft brightness falloff. The high
            //    resolutions (alpha = 1) replace, so that no details return.
            for (int i = data.passCount - 1; i > 0; i--)
            {
                Blitter.BlitCameraTexture(cmd, data.temps[i], data.temps[i - 1],
                    RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, data.upsampleMaterials[i], 0);
            }
        }


        private class PassData
        {
            internal TextureHandle source;
            internal TextureHandle[] temps;
            internal int passCount;
            internal Material prepassMaterial;
            internal Material downsampleMaterial;
            internal Material[] upsampleMaterials;
        }
    }
}
