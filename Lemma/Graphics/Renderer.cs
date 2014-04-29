﻿using System; using ComponentBind;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework;

namespace Lemma.Components
{
	public enum Technique { Render, Shadow, NonPostProcessed, Clip };

	public class RenderParameters
	{
		public Camera Camera;
		private Plane[] clipPlanes;
		public Plane[] ClipPlanes
		{
			get
			{
				return this.clipPlanes;
			}
			set
			{
				this.clipPlanes = value;
				if (value == null)
					this.ClipPlaneData = new Vector4[] { Vector4.Zero, Vector4.Zero, Vector4.Zero, Vector4.Zero };
				else
					this.ClipPlaneData = value.Select(x => new Vector4(x.Normal, x.D)).ToArray();
			}
		}
		public Vector4[] ClipPlaneData;
		public Technique Technique;
		public bool ReverseCullOrder;
		public RenderTarget2D DepthBuffer;
		public RenderTarget2D FrameBuffer;
		public bool IsMainRender;
		public static readonly RenderParameters Default = new RenderParameters();
		public RenderParameters Clone()
		{
			return new RenderParameters
			{
				Camera = this.Camera,
				clipPlanes = (Plane[])this.clipPlanes.Clone(),
				ClipPlaneData = (Vector4[])this.ClipPlaneData.Clone(),
				Technique = this.Technique,
				ReverseCullOrder = this.ReverseCullOrder,
				DepthBuffer = this.DepthBuffer,
				FrameBuffer = this.FrameBuffer,
				IsMainRender = this.IsMainRender,
			};
		}
	}

	/// <summary>
	/// Deferred renderer
	/// </summary>
	public class Renderer : Component<Main>, IGraphicsComponent
	{
		private LightingManager lightingManager;

		// Geometry
		private static FullscreenQuad quad;
		private static Microsoft.Xna.Framework.Graphics.Model pointLightModel;
		private static Microsoft.Xna.Framework.Graphics.Model spotLightModel;

		public Property<float> BlurAmount = new Property<float>();
		public Property<float> SpeedBlurAmount = new Property<float> { Value = 0.0f };
		public Property<Vector3> Tint = new Property<Vector3> { Value = Vector3.One };
		public Property<float> InternalGamma = new Property<float> { Value = 0.0f };
		public Property<float> Gamma = new Property<float> { Value = 1.0f };
		public Property<float> Brightness = new Property<float> { Value = 0.0f };
		public Property<float> MotionBlurAmount = new Property<float> { Value = 1.0f };
		private Texture2D lightRampTexture;
		private TextureCube environmentMap;
		public Property<string> LightRampTexture = new Property<string>();
		public Property<string> EnvironmentMap = new Property<string>();
		public Property<Vector3> EnvironmentColor = new Property<Vector3> { Value = Vector3.One };
		public Property<bool> EnableBloom = new Property<bool> { Value = true };
		public Property<bool> EnableHighResLighting = new Property<bool> { Value = true };
		public Property<bool> EnableSSAO = new Property<bool> { Value = true };

		public static readonly Color DefaultBackgroundColor = new Color(16.0f / 255.0f, 26.0f / 255.0f, 38.0f / 255.0f, 0.0f);
		public Property<Color> BackgroundColor = new Property<Color> { Value = Renderer.DefaultBackgroundColor };

		private Point screenSize;

		// Effects
		private static Effect globalLightEffect;
		private static Effect pointLightEffect;
		private static Effect spotLightEffect;
		private Effect compositeEffect;
		private Effect motionBlurEffect;
		private Effect bloomEffect;
		private Effect blurEffect;
		private Effect clearEffect;
		private Effect downsampleEffect;
		private Effect ssaoEffect;

		// Render targets
		private RenderTarget2D lightingBuffer;
		private RenderTarget2D specularBuffer;
		private RenderTarget2D depthBuffer;
		private RenderTarget2D normalBuffer;
		private RenderTarget2D colorBuffer1;
		private RenderTarget2D colorBuffer2;
		private RenderTarget2D hdrBuffer1;
		private RenderTarget2D hdrBuffer2;
		private RenderTarget2D halfBuffer1;
		private RenderTarget2D halfBuffer2;
		private RenderTarget2D halfDepthBuffer;
		private Texture2D ssaoRandomTexture;
		private bool allowSSAO;
		private RenderTarget2D normalBufferLastFrame;
		private bool allowBloom;
		private bool allowPostAlphaDrawables;
		private SpriteBatch spriteBatch;

		/// <summary>
		/// The class constructor
		/// </summary>
		/// <param name="graphicsDevice">The GraphicsDevice to use for rendering</param>
		/// <param name="contentManager">The ContentManager from which to load Effects</param>
		public Renderer(Main main, Point size, bool allowHdr, bool allowBloom, bool allowSSAO, bool allowPostAlphaDrawables)
		{
			this.allowBloom = allowBloom;
			this.allowSSAO = allowSSAO;
			this.allowPostAlphaDrawables = allowPostAlphaDrawables;
			this.hdr = allowHdr;
			this.lightingManager = main.LightingManager;
			this.screenSize = size;
		}

		public override void InitializeProperties()
		{
			base.InitializeProperties();

			this.BlurAmount.Set = delegate(float value)
			{
				this.BlurAmount.InternalValue = value;
				this.blurEffect.Parameters["BlurAmount"].SetValue(value);
			};
			this.LightRampTexture.Set = delegate(string file)
			{
				if (this.LightRampTexture.InternalValue != file)
				{
					this.LightRampTexture.InternalValue = file;
					this.loadLightRampTexture(file);
				}
			};

			this.EnvironmentMap.Set = delegate(string file)
			{
				if (this.EnvironmentMap.InternalValue != file)
				{
					this.EnvironmentMap.InternalValue = file;
					this.loadEnvironmentMap(file);
				}
			};

			this.InternalGamma.Set = delegate(float value)
			{
				this.InternalGamma.InternalValue = value;
				this.Gamma.Reset();
			};

			this.EnvironmentColor.Set = delegate(Vector3 value)
			{
				this.EnvironmentColor.InternalValue = value;
				Renderer.globalLightEffect.Parameters["EnvironmentColor"].SetValue(value);
			};

			this.MotionBlurAmount.Set = delegate(float value)
			{
				this.MotionBlurAmount.InternalValue = value;
				this.motionBlurEffect.Parameters["MotionBlurAmount"].SetValue(value);
			};
			this.SpeedBlurAmount.Set = delegate(float value)
			{
				this.SpeedBlurAmount.InternalValue = value;
				this.motionBlurEffect.Parameters["SpeedBlurAmount"].SetValue(value);
			};
			this.SpeedBlurAmount.Value = 0.0f;

			this.Gamma.Set = delegate(float value)
			{
				this.Gamma.InternalValue = value;
				this.bloomEffect.Parameters["Gamma"].SetValue(value + this.InternalGamma);
			};
			this.Tint.Set = delegate(Vector3 value)
			{
				this.Tint.InternalValue = value;
				this.bloomEffect.Parameters["Tint"].SetValue(value);
			};
			this.Brightness.Set = delegate(float value)
			{
				this.Brightness.InternalValue = value;
				this.bloomEffect.Parameters["Brightness"].SetValue(value);
			};

			this.EnableHighResLighting.Set = delegate(bool value)
			{
				if (value != this.EnableHighResLighting.InternalValue)
				{
					this.EnableHighResLighting.InternalValue = value;
					this.ReallocateBuffers(this.main.ScreenSize);
				}
			};
		}

		private void loadLightRampTexture(string file)
		{
			this.lightRampTexture = file == null ? (Texture2D)null : this.main.Content.Load<Texture2D>(file);
			this.bloomEffect.Parameters["Ramp" + Model.SamplerPostfix].SetValue(this.lightRampTexture);
		}

		private void loadEnvironmentMap(string file)
		{
			this.environmentMap = file == null ? (TextureCube)null : this.main.Content.Load<TextureCube>(file);
			Renderer.globalLightEffect.Parameters["Environment" + Model.SamplerPostfix].SetValue(this.environmentMap);
		}

		public void LoadContent(bool reload)
		{
			// Load static resources
			if (reload)
				Renderer.quad.LoadContent(true);
			else if (Renderer.quad == null)
			{
				Renderer.quad = new FullscreenQuad();
				Renderer.quad.SetMain(this.main);
				Renderer.quad.LoadContent(false);
				Renderer.quad.InitializeProperties();
			}

			this.spriteBatch = new SpriteBatch(this.main.GraphicsDevice);

			if (Renderer.globalLightEffect == null || reload)
			{
				Renderer.globalLightEffect = this.main.Content.Load<Effect>("Effects\\PostProcess\\GlobalLight");
				this.loadEnvironmentMap(this.EnvironmentMap);
				Renderer.pointLightEffect = this.main.Content.Load<Effect>("Effects\\PostProcess\\PointLight");
				Renderer.spotLightEffect = this.main.Content.Load<Effect>("Effects\\PostProcess\\SpotLight");
			}

			if (Renderer.pointLightModel == null || reload)
			{
				// Load light models
				Renderer.pointLightModel = this.main.Content.Load<Microsoft.Xna.Framework.Graphics.Model>("Models\\pointlight");
				Renderer.spotLightModel = this.main.Content.Load<Microsoft.Xna.Framework.Graphics.Model>("Models\\spotlight");
			}

			this.compositeEffect = this.main.Content.Load<Effect>("Effects\\PostProcess\\Composite").Clone();
			this.compositeEffect.CurrentTechnique = this.compositeEffect.Techniques["Composite"];
			this.blurEffect = this.main.Content.Load<Effect>("Effects\\PostProcess\\Blur").Clone();

			this.downsampleEffect = this.main.Content.Load<Effect>("Effects\\PostProcess\\Downsample").Clone();
			this.ssaoEffect = this.main.Content.Load<Effect>("Effects\\PostProcess\\SSAO").Clone();
			this.ssaoRandomTexture = this.main.Content.Load<Texture2D>("Images\\random");
			this.ssaoEffect.Parameters["Random" + Model.SamplerPostfix].SetValue(this.ssaoRandomTexture);

			this.bloomEffect = this.main.Content.Load<Effect>("Effects\\PostProcess\\Bloom").Clone();

			this.loadLightRampTexture(this.LightRampTexture);

			this.clearEffect = this.main.Content.Load<Effect>("Effects\\PostProcess\\Clear").Clone();

			this.motionBlurEffect = this.main.Content.Load<Effect>("Effects\\PostProcess\\MotionBlur").Clone();

			// Initialize our buffers
			this.ReallocateBuffers(this.screenSize);
		}

		private bool hdr;

		private SurfaceFormat hdrSurfaceFormat
		{
			get
			{
				return this.hdr ? SurfaceFormat.HdrBlendable : SurfaceFormat.Color;
			}
		}

		private SurfaceFormat lightingSurfaceFormat
		{
			get
			{
				return this.hdr && this.EnableHighResLighting ? SurfaceFormat.HdrBlendable : SurfaceFormat.Color;
			}
		}

		public void ReallocateBuffers(Point size)
		{
			this.screenSize = size;
			// Lighting buffer
			if (this.lightingBuffer != null && !this.lightingBuffer.IsDisposed)
				this.lightingBuffer.Dispose();
			this.lightingBuffer = new RenderTarget2D(this.main.GraphicsDevice,
												size.X,
												size.Y,
												false,
												this.lightingSurfaceFormat,
												DepthFormat.None,
												0,
												RenderTargetUsage.DiscardContents);

			// Specular lighting buffer
			if (this.specularBuffer != null && !this.specularBuffer.IsDisposed)
				this.specularBuffer.Dispose();
			this.specularBuffer = new RenderTarget2D(this.main.GraphicsDevice,
												size.X,
												size.Y,
												false,
												this.lightingSurfaceFormat,
												DepthFormat.None,
												0,
												RenderTargetUsage.DiscardContents);

			// Depth buffer
			if (this.depthBuffer != null && !this.depthBuffer.IsDisposed)
				this.depthBuffer.Dispose();
			this.depthBuffer = new RenderTarget2D(this.main.GraphicsDevice,
												size.X,
												size.Y,
												false,
												SurfaceFormat.HalfVector2,
												DepthFormat.Depth24,
												0,
												RenderTargetUsage.DiscardContents);

			// Normal buffer
			if (this.normalBuffer != null && !this.normalBuffer.IsDisposed)
				this.normalBuffer.Dispose();
			this.normalBuffer = new RenderTarget2D(this.main.GraphicsDevice,
												size.X,
												size.Y,
												false,
												SurfaceFormat.Color,
												DepthFormat.None,
												0,
												RenderTargetUsage.DiscardContents);

			// Color buffer 1
			if (this.colorBuffer1 != null && !this.colorBuffer1.IsDisposed)
				this.colorBuffer1.Dispose();
			this.colorBuffer1 = new RenderTarget2D(this.main.GraphicsDevice,
												size.X,
												size.Y,
												false,
												SurfaceFormat.Color,
												DepthFormat.Depth24,
												0,
												RenderTargetUsage.DiscardContents);

			// Color buffer 2
			if (this.colorBuffer2 != null && !this.colorBuffer2.IsDisposed)
				this.colorBuffer2.Dispose();
			this.colorBuffer2 = new RenderTarget2D(this.main.GraphicsDevice,
												size.X,
												size.Y,
												false,
												SurfaceFormat.Color,
												DepthFormat.Depth24,
												0,
												RenderTargetUsage.DiscardContents);

			if (this.hdr)
			{
				// HDR buffer 1
				if (this.hdrBuffer1 != null && !this.hdrBuffer1.IsDisposed)
					this.hdrBuffer1.Dispose();
				this.hdrBuffer1 = new RenderTarget2D(this.main.GraphicsDevice,
													size.X,
													size.Y,
													false,
													this.hdrSurfaceFormat,
													DepthFormat.Depth24,
													0,
													RenderTargetUsage.DiscardContents);

				// HDR buffer 2
				if (this.hdrBuffer2 != null && !this.hdrBuffer2.IsDisposed)
					this.hdrBuffer2.Dispose();
				this.hdrBuffer2 = new RenderTarget2D(this.main.GraphicsDevice,
													size.X,
													size.Y,
													false,
													this.hdrSurfaceFormat,
													DepthFormat.None,
													0,
													RenderTargetUsage.DiscardContents);
			}
			else
			{
				this.hdrBuffer1 = this.colorBuffer1;
				this.hdrBuffer2 = this.colorBuffer2;
			}

			if (this.normalBufferLastFrame != null)
			{
				if (!this.normalBufferLastFrame.IsDisposed)
					this.normalBufferLastFrame.Dispose();
				this.normalBufferLastFrame = null;
			}

			// Normal buffer from last frame
			this.normalBufferLastFrame = new RenderTarget2D(this.main.GraphicsDevice,
												size.X,
												size.Y,
												false,
												SurfaceFormat.Color,
												DepthFormat.None,
												0,
												RenderTargetUsage.DiscardContents);

			if (this.halfBuffer1 != null)
			{
				if (!this.halfBuffer1.IsDisposed)
					this.halfBuffer1.Dispose();
				this.halfBuffer1 = null;
			}
			if (this.halfBuffer2 != null)
			{
				if (!this.halfBuffer2.IsDisposed)
					this.halfBuffer2.Dispose();
				this.halfBuffer2 = null;
			}
			if (this.halfDepthBuffer != null)
			{
				if (!this.halfDepthBuffer.IsDisposed)
					this.halfDepthBuffer.Dispose();
				this.halfDepthBuffer = null;
			}

			if (this.allowBloom || this.allowSSAO)
			{
				this.halfBuffer1 = new RenderTarget2D(this.main.GraphicsDevice,
					size.X / 2,
					size.Y / 2,
					false,
					SurfaceFormat.Color,
					DepthFormat.None,
					0,
					RenderTargetUsage.DiscardContents);
				this.halfBuffer2 = new RenderTarget2D(this.main.GraphicsDevice,
					size.X / 2,
					size.Y / 2,
					false,
					SurfaceFormat.Color,
					DepthFormat.None,
					0,
					RenderTargetUsage.DiscardContents);
			}

			if (this.allowSSAO)
			{
				this.halfDepthBuffer = new RenderTarget2D(this.main.GraphicsDevice,
					size.X / 2,
					size.Y / 2,
					false,
					SurfaceFormat.Single,
					DepthFormat.None,
					0,
					RenderTargetUsage.DiscardContents);
			}
		}

		public void SetRenderTargets(RenderParameters p)
		{
			this.main.GraphicsDevice.SetRenderTargets(this.colorBuffer1, this.depthBuffer, this.normalBuffer);
			this.clearEffect.CurrentTechnique = this.clearEffect.Techniques["Clear"];
			Color color = this.BackgroundColor;
			p.Camera.SetParameters(this.clearEffect);
			this.setTargetParameters(new RenderTarget2D[] { }, new RenderTarget2D[] { this.colorBuffer1 }, this.clearEffect);
			this.clearEffect.Parameters["BackgroundColor"].SetValue(new Vector3((float)color.R / 255.0f, (float)color.G / 255.0f, (float)color.B / 255.0f));
			this.main.GraphicsDevice.SamplerStates[1] = SamplerState.PointClamp;
			this.main.GraphicsDevice.SamplerStates[2] = SamplerState.PointClamp;
			this.main.GraphicsDevice.SamplerStates[3] = SamplerState.PointClamp;
			this.main.GraphicsDevice.SamplerStates[4] = SamplerState.PointClamp;
			this.applyEffect(this.clearEffect);
			Renderer.quad.DrawAlpha(this.main.GameTime, RenderParameters.Default);
		}

		public void PostProcess(RenderTarget2D result, RenderParameters parameters)
		{
			Vector3 originalCameraPosition = parameters.Camera.Position;
			Matrix originalViewMatrix = parameters.Camera.View;
			BoundingFrustum originalBoundingFrustum = parameters.Camera.BoundingFrustum;

			parameters.Camera.Position.Value = Vector3.Zero;
			Matrix newViewMatrix = originalViewMatrix;
			newViewMatrix.Translation = Vector3.Zero;
			parameters.Camera.View.Value = newViewMatrix;

			RasterizerState originalState = this.main.GraphicsDevice.RasterizerState;
			RasterizerState reverseCullState = new RasterizerState { CullMode = CullMode.CullClockwiseFace };

			bool enableSSAO = this.allowSSAO && this.EnableSSAO;

			if (enableSSAO)
			{
				// Down-sample depth buffer
				this.downsampleEffect.CurrentTechnique = this.downsampleEffect.Techniques["DownsampleDepth"];
				this.preparePostProcess(new[] { this.depthBuffer, this.normalBuffer }, new[] { this.halfDepthBuffer, this.halfBuffer1 }, this.downsampleEffect);
				Renderer.quad.DrawAlpha(this.main.GameTime, RenderParameters.Default);

				// Compute SSAO
				parameters.Camera.SetParameters(this.ssaoEffect);
				this.ssaoEffect.CurrentTechnique = this.ssaoEffect.Techniques["SSAO"];
				this.preparePostProcess(new[] { this.halfDepthBuffer, this.halfBuffer1 }, new[] { this.halfBuffer2 }, this.ssaoEffect);
				this.main.GraphicsDevice.Clear(Color.Black);
				Renderer.quad.DrawAlpha(this.main.GameTime, RenderParameters.Default);

				// Blur
				this.ssaoEffect.CurrentTechnique = this.ssaoEffect.Techniques["BlurHorizontal"];
				this.preparePostProcess(new RenderTarget2D[] { this.halfBuffer2, this.halfDepthBuffer }, new RenderTarget2D[] { this.halfBuffer1 }, this.ssaoEffect);
				Renderer.quad.DrawAlpha(this.main.GameTime, RenderParameters.Default);

				this.ssaoEffect.CurrentTechnique = this.ssaoEffect.Techniques["Composite"];
				this.preparePostProcess(new RenderTarget2D[] { this.halfBuffer1, this.halfDepthBuffer }, new RenderTarget2D[] { this.halfBuffer2 }, this.ssaoEffect);
				Renderer.quad.DrawAlpha(this.main.GameTime, RenderParameters.Default);
			}

			// Global lighting
			this.setTargets(this.lightingBuffer, this.specularBuffer);
			string globalLightTechnique = "GlobalLight";
			if (this.lightingManager.EnableGlobalShadowMap && this.lightingManager.HasGlobalShadowLight)
			{
				if (this.lightingManager.EnableDetailGlobalShadowMap)
					globalLightTechnique = "GlobalLightDetailShadow";
				else
					globalLightTechnique = "GlobalLightShadow";
			}
			Renderer.globalLightEffect.CurrentTechnique = Renderer.globalLightEffect.Techniques[globalLightTechnique];
			parameters.Camera.SetParameters(Renderer.globalLightEffect);
			this.lightingManager.SetGlobalLightParameters(Renderer.globalLightEffect, parameters.Camera, originalCameraPosition);
			this.lightingManager.SetMaterialParameters(Renderer.globalLightEffect);
			this.setTargetParameters(new RenderTarget2D[] { this.depthBuffer, this.normalBuffer, this.colorBuffer1 }, new RenderTarget2D[] { this.lightingBuffer, this.specularBuffer }, Renderer.globalLightEffect);
			this.applyEffect(Renderer.globalLightEffect);
			Renderer.quad.DrawAlpha(this.main.GameTime, RenderParameters.Default);

			// Spot and point lights
			if (!parameters.ReverseCullOrder)
				this.main.GraphicsDevice.RasterizerState = reverseCullState;

			// HACK
			// Increase the far plane to prevent clipping back faces of huge lights
			float originalFarPlane = parameters.Camera.FarPlaneDistance;
			parameters.Camera.FarPlaneDistance.Value *= 3.0f;
			parameters.Camera.SetParameters(Renderer.pointLightEffect);
			parameters.Camera.SetParameters(Renderer.spotLightEffect);
			parameters.Camera.FarPlaneDistance.Value = originalFarPlane;

			// Spot lights
			this.lightingManager.SetMaterialParameters(Renderer.spotLightEffect);
			this.setTargetParameters(new RenderTarget2D[] { this.depthBuffer, this.normalBuffer, this.colorBuffer1 }, new RenderTarget2D[] { this.lightingBuffer, this.specularBuffer }, Renderer.spotLightEffect);
			for (int i = 0; i < SpotLight.All.Count; i++)
			{
				SpotLight light = SpotLight.All[i];
				if (!light.Enabled || light.Suspended || light.Attenuation == 0.0f || light.Color.Value.LengthSquared() == 0.0f || !originalBoundingFrustum.Intersects(light.BoundingFrustum))
					continue;

				this.lightingManager.SetSpotLightParameters(light, Renderer.spotLightEffect, originalCameraPosition);
				this.applyEffect(Renderer.spotLightEffect);
				this.drawModel(Renderer.spotLightModel);
			}

			// Point lights
			this.lightingManager.SetMaterialParameters(Renderer.pointLightEffect);
			this.setTargetParameters(new RenderTarget2D[] { this.depthBuffer, this.normalBuffer, this.colorBuffer1 }, new RenderTarget2D[] { this.lightingBuffer, this.specularBuffer }, Renderer.pointLightEffect);
			for (int i = 0; i < PointLight.All.Count; i++)
			{
				PointLight light = PointLight.All[i];
				if (!light.Enabled || light.Suspended || light.Attenuation == 0.0f || light.Color.Value.LengthSquared() == 0.0f || !originalBoundingFrustum.Intersects(light.BoundingSphere))
					continue;
				this.lightingManager.SetPointLightParameters(light, Renderer.pointLightEffect, originalCameraPosition);
				this.applyEffect(Renderer.pointLightEffect);
				this.drawModel(Renderer.pointLightModel);
			}

			if (!parameters.ReverseCullOrder)
				this.main.GraphicsDevice.RasterizerState = originalState;

			RenderTarget2D colorSource = this.colorBuffer1;
			RenderTarget2D colorDestination = this.hdrBuffer2;
			RenderTarget2D colorTemp = null;

			// Compositing
			this.compositeEffect.CurrentTechnique = this.compositeEffect.Techniques["Composite" + (enableSSAO ? "SSAO" : "")];
			this.lightingManager.SetCompositeParameters(this.compositeEffect);
			parameters.Camera.SetParameters(this.compositeEffect);
			this.lightingManager.SetMaterialParameters(this.compositeEffect);
			this.preparePostProcess
			(
				enableSSAO
				? new RenderTarget2D[] { colorSource, this.lightingBuffer, this.specularBuffer, this.halfBuffer2 }
				: new RenderTarget2D[] { colorSource, this.lightingBuffer, this.specularBuffer },
				new RenderTarget2D[] { colorDestination },
				this.compositeEffect
			);
			Renderer.quad.DrawAlpha(this.main.GameTime, RenderParameters.Default);

			bool enableBloom = this.allowBloom && this.EnableBloom;
			bool enableMotionBlur = this.MotionBlurAmount > 0.0f;
			bool enableBlur = this.BlurAmount > 0.0f;

			// Swap the color buffers
			colorSource = this.hdrBuffer2;
			colorDestination = this.hdrBuffer1;

			parameters.DepthBuffer = this.depthBuffer;
			parameters.FrameBuffer = colorSource;

			// Alpha components

			// Drawing to the color destination
			this.setTargets(colorDestination);

			// Copy the color source to the destination
			this.spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp, DepthStencilState.None, originalState);
			this.spriteBatch.Draw(colorSource, Vector2.Zero, Color.White);
			this.spriteBatch.End();

			parameters.Camera.Position.Value = originalCameraPosition;
			parameters.Camera.View.Value = originalViewMatrix;

			this.main.DrawAlphaComponents(parameters);
			this.main.DrawPostAlphaComponents(parameters);

			// Swap the color buffers
			colorTemp = colorDestination;
			colorDestination = colorSource;
			parameters.FrameBuffer = colorSource = colorTemp;

			// Bloom
			if (enableBloom)
			{
				this.bloomEffect.CurrentTechnique = this.bloomEffect.Techniques["Downsample"];
				this.preparePostProcess(new[] { colorSource }, new[] { this.halfBuffer1 }, this.bloomEffect);
				Renderer.quad.DrawAlpha(this.main.GameTime, RenderParameters.Default);

				this.bloomEffect.CurrentTechnique = this.bloomEffect.Techniques["BlurHorizontal"];
				this.preparePostProcess(new RenderTarget2D[] { this.halfBuffer1 }, new RenderTarget2D[] { this.halfBuffer2 }, this.bloomEffect);
				Renderer.quad.DrawAlpha(this.main.GameTime, RenderParameters.Default);

				this.bloomEffect.CurrentTechnique = this.bloomEffect.Techniques["BlurVertical"];
				this.preparePostProcess(new RenderTarget2D[] { this.halfBuffer2 }, new RenderTarget2D[] { this.halfBuffer1 }, this.bloomEffect);
				Renderer.quad.DrawAlpha(this.main.GameTime, RenderParameters.Default);

				this.bloomEffect.CurrentTechnique = this.bloomEffect.Techniques["Composite"];
				this.preparePostProcess(new RenderTarget2D[] { colorSource, this.halfBuffer1 }, new RenderTarget2D[] { enableBlur || enableMotionBlur ? this.colorBuffer2 : result }, this.bloomEffect);
				Renderer.quad.DrawAlpha(this.main.GameTime, RenderParameters.Default);
			}
			else
			{
				this.bloomEffect.CurrentTechnique = this.bloomEffect.Techniques["ToneMapOnly"];
				this.preparePostProcess(new RenderTarget2D[] { colorSource, }, new RenderTarget2D[] { enableBlur || enableMotionBlur ? this.colorBuffer2 : result }, this.bloomEffect);
				Renderer.quad.DrawAlpha(this.main.GameTime, RenderParameters.Default);
			}

			// Swap the color buffers
			colorDestination = this.colorBuffer1;
			colorSource = this.colorBuffer2;

			// Motion blur
			if (enableMotionBlur)
			{
				this.motionBlurEffect.CurrentTechnique = this.motionBlurEffect.Techniques["MotionBlur"];
				parameters.Camera.SetParameters(this.motionBlurEffect);
				this.preparePostProcess(new RenderTarget2D[] { colorSource, this.normalBuffer, this.normalBufferLastFrame }, new RenderTarget2D[] { enableBlur ? colorDestination : result }, this.motionBlurEffect);
				Renderer.quad.DrawAlpha(this.main.GameTime, RenderParameters.Default);

				// Swap the velocity buffers
				RenderTarget2D temp = this.normalBufferLastFrame;
				this.normalBufferLastFrame = this.normalBuffer;
				this.normalBuffer = temp;

				// Swap the color buffers
				colorTemp = colorDestination;
				colorDestination = colorSource;
				colorSource = colorTemp;
			}

			if (enableBlur)
			{
				// Blur
				this.blurEffect.CurrentTechnique = this.blurEffect.Techniques["BlurHorizontal"];
				parameters.Camera.SetParameters(this.blurEffect);
				this.preparePostProcess(new RenderTarget2D[] { colorSource }, new RenderTarget2D[] { colorDestination }, this.blurEffect);
				Renderer.quad.DrawAlpha(this.main.GameTime, RenderParameters.Default);
				this.blurEffect.CurrentTechnique = this.blurEffect.Techniques["Composite"];

				// Swap the color buffers
				colorTemp = colorDestination;
				colorDestination = colorSource;
				colorSource = colorTemp;

				this.preparePostProcess(new RenderTarget2D[] { colorSource }, new RenderTarget2D[] { result }, this.blurEffect);
				Renderer.quad.DrawAlpha(this.main.GameTime, RenderParameters.Default);
			}

			parameters.DepthBuffer = null;
			parameters.FrameBuffer = null;
		}

		private void drawModel(Microsoft.Xna.Framework.Graphics.Model model)
		{
			foreach (ModelMesh mesh in model.Meshes)
			{
				foreach (ModelMeshPart part in mesh.MeshParts)
				{
					if (part.NumVertices > 0)
					{
						this.main.GraphicsDevice.SetVertexBuffer(part.VertexBuffer);
						this.main.GraphicsDevice.Indices = part.IndexBuffer;
						this.main.GraphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, part.NumVertices, part.StartIndex, part.PrimitiveCount);
						Model.DrawCallCounter++;
						Model.TriangleCounter += part.PrimitiveCount;
					}
				}
			}
		}

		private void setTargets(params RenderTarget2D[] results)
		{
			if (results == null)
				this.main.GraphicsDevice.SetRenderTarget(null);
			else if (results.Length == 1)
				this.main.GraphicsDevice.SetRenderTarget(results[0]);
			else
				this.main.GraphicsDevice.SetRenderTargets(results.Select(x => new RenderTargetBinding(x)).ToArray());
		}

		private void preparePostProcess(RenderTarget2D[] sources, RenderTarget2D[] results, Effect effect)
		{
			this.setTargets(results);
			this.setTargetParameters(sources, results, effect);
			this.applyEffect(effect);
		}

		private void setTargetParameters(RenderTarget2D[] sources, RenderTarget2D[] results, Effect effect)
		{
			EffectParameter param;
			for (int i = 0; i < sources.Length; i++)
			{
				param = effect.Parameters["Source" + Model.SamplerPostfix + i.ToString()];
				if (param == null)
					break;
				param.SetValue(sources[i]);
				param = effect.Parameters["SourceDimensions" + i.ToString()];
				if (param != null)
					param.SetValue(new Vector2(sources[i].Width, sources[i].Height));
			}
			param = effect.Parameters["DestinationDimensions"];
			if (param != null)
			{
				if (results == null || results.Length == 0 || results[0] == null)
					param.SetValue(new Vector2(this.screenSize.X, this.screenSize.Y));
				else
					param.SetValue(new Vector2(results[0].Width, results[0].Height));
			}
		}

		private void applyEffect(Effect effect)
		{
			effect.CurrentTechnique.Passes[0].Apply();
		}

		public override void delete()
		{
			base.delete();
			this.lightingBuffer.Dispose();
			this.normalBuffer.Dispose();
			this.normalBufferLastFrame.Dispose();
			this.depthBuffer.Dispose();
			this.colorBuffer1.Dispose();
			this.colorBuffer2.Dispose();
			if (this.hdr)
			{
				this.hdrBuffer1.Dispose();
				this.hdrBuffer2.Dispose();
			}
			this.specularBuffer.Dispose();

			this.compositeEffect.Dispose();
			this.blurEffect.Dispose();
			this.clearEffect.Dispose();

			if (this.motionBlurEffect != null)
				this.motionBlurEffect.Dispose();

			if (this.halfDepthBuffer != null)
				this.halfDepthBuffer.Dispose();
			if (this.halfBuffer1 != null)
				this.halfBuffer1.Dispose();
			if (this.halfBuffer2 != null)
				this.halfBuffer2.Dispose();

			if (this.bloomEffect != null)
				this.bloomEffect.Dispose();
		}
	}
}
