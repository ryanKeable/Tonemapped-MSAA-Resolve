Shader "DelMarRenderer/ColorResolve"
{
    HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // #define COLOR_TEXTURE_MS(name, samples) Texture2DMSArray<float4, samples> name        
        // #define LOAD(uv, sampleIndex) LOAD_TEXTURE2D_ARRAY_MSAA(_CameraColorAttachment, uv, unity_StereoEyeIndex, sampleIndex)

        #define COLOR_TEXTURE_MS(name, samples) Texture2DMS<float4, samples> name
        #define LOAD(uv, sampleIndex) LOAD_TEXTURE2D_MSAA(_CameraColorAttachment, uv, sampleIndex)

        #define SAMPLES 4.0

        COLOR_TEXTURE_MS(_CameraColorAttachment, 4); // 4 = MSAA SAMPLES
        float4 _CameraColorAttachment_TexelSize;

        struct Attributes
        {
            float4 positionOS   : POSITION;
            float2 uv           : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS   : SV_POSITION;
            float2 uv           : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings Vert(Attributes input)
        {
            Varyings output;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            output.uv = input.uv;
            output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            return output;
        }

        float ResolveWeight(float4 color, float totalSampleCount)
        {
            const float boxFilterWeight = rcp(totalSampleCount);

            return boxFilterWeight;
        }

        float LuminanceToneMap(float4 color)
        {
            return rcp(1.0f + Luminance(color.xyz));
        }

        float DoFastToneMap(float4 color)
        {
            return FastTonemap(color);
        }


        float ResolveWeightTonemapped(float4 color, float totalSampleCount)
        {
            const float boxFilterWeight = rcp(totalSampleCount);
            float toneMapWeight = LuminanceToneMap(color);

            return boxFilterWeight * toneMapWeight;
        }

        float InverseToneMapWeight(float4 color)
        {
            return rcp(1.0f - Luminance(color.xyz));
        }

        float4 LoadColorTextureMS(float2 pixelCoords, uint sampleIndex)
        {
            return LOAD(pixelCoords, sampleIndex);
        }

        float4 FragMSAAResolve(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            half2 uv = UnityStereoTransformScreenSpaceTex(input.uv);
            int2 coord = int2(uv * _CameraColorAttachment_TexelSize.zw);
            float4 color = 0.0;

            UNITY_UNROLL
            for (int i = 0; i < SAMPLES; ++i)
            {
                float4 currSample = (LoadColorTextureMS(coord, i));
                color.rgb += currSample.rgb * ResolveWeightTonemapped(currSample, SAMPLES);
                color.a += currSample.a * rcp(SAMPLES);
            }

            color.xyz *= InverseToneMapWeight(color);

            return color;
        }

    ENDHLSL
    SubShader
    {
        Tags{ "RenderPipeline" = "HDRenderPipeline" }

        Pass
        {
            ZWrite Off ZTest Always Blend Off Cull Off

            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment FragMSAAResolve
                
                #pragma fragmentoption ARB_precision_hint_fastest
            ENDHLSL
        }
    }
    Fallback Off
}
