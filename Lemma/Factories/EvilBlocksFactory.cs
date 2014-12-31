﻿using System; using ComponentBind;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Lemma.Components;
using Lemma.Util;
using Microsoft.Xna.Framework.Audio;
using BEPUphysics.BroadPhaseEntries;
using BEPUphysics.NarrowPhaseSystems.Pairs;

namespace Lemma.Factories
{
	public class EvilBlocksFactory : Factory<Main>
	{
		private Random random = new Random();

		public EvilBlocksFactory()
		{
			this.Color = new Vector3(1.0f, 1.0f, 0.7f);
		}

		public override Entity Create(Main main)
		{
			return new Entity(main, "EvilBlocks");
		}

		public override void Bind(Entity entity, Main main, bool creating = false)
		{
			BlockCloud blockCloud = entity.GetOrCreate<BlockCloud>("BlockCloud");
			blockCloud.Add(new CommandBinding(blockCloud.Delete, entity.Delete));
			blockCloud.Add(new CommandBinding<Collidable, ContactCollection>(blockCloud.Collided, delegate(Collidable other, ContactCollection contacts)
			{
				if (other.Tag != null && other.Tag.GetType() == typeof(Character))
				{
					// Damage the player
					Entity p = PlayerFactory.Instance;
					if (p != null && p.Active)
						p.Get<Agent>().Damage.Execute(0.1f);
				}
			}));
			blockCloud.Type.Value = Voxel.t.Black;

			Transform transform = entity.GetOrCreate<Transform>("Transform");

			AkGameObjectTracker.Attach(entity);

			AI ai = entity.GetOrCreate<AI>("AI");

			if (!main.EditorEnabled)
			{
				entity.Add(new PostInitialization
				{
					delegate()
					{
						AkSoundEngine.PostEvent(AK.EVENTS.PLAY_EVIL_CUBES, entity);
						AkSoundEngine.PostEvent(ai.CurrentState == "Chase" ? AK.EVENTS.EVIL_CUBES_CHASE : AK.EVENTS.EVIL_CUBES_IDLE, entity);
					}
				});

				SoundKiller.Add(entity, AK.EVENTS.STOP_EVIL_CUBES);
			}

			Agent agent = entity.GetOrCreate<Agent>();
			agent.Add(new Binding<Vector3>(agent.Position, transform.Position));

			RaycastAI raycastAI = entity.GetOrCreate<RaycastAI>("RaycastAI");
			raycastAI.BlendTime.Value = 1.0f;
			raycastAI.Add(new TwoWayBinding<Vector3>(transform.Position, raycastAI.Position));
			raycastAI.Add(new Binding<Quaternion>(transform.Quaternion, raycastAI.Orientation));

			blockCloud.Add(new Binding<Vector3>(blockCloud.Position, transform.Position));

			const float operationalRadius = 100.0f;
			AI.Task checkOperationalRadius = new AI.Task
			{
				Interval = 2.0f,
				Action = delegate()
				{
					bool shouldBeActive = (transform.Position.Value - main.Camera.Position).Length() < operationalRadius;
					if (shouldBeActive && ai.CurrentState == "Suspended")
						ai.CurrentState.Value = "Idle";
					else if (!shouldBeActive && ai.CurrentState != "Suspended")
						ai.CurrentState.Value = "Suspended";
				},
			};

			AI.Task updatePosition = new AI.Task
			{
				Action = delegate()
				{
					raycastAI.Update();
				},
			};

			ai.Add(new AI.AIState
			{
				Name = "Suspended",
				Tasks = new[] { checkOperationalRadius, },
			});

			const float sightDistance = 30.0f;
			const float hearingDistance = 15.0f;

			ai.Add(new AI.AIState
			{
				Name = "Idle",
				Tasks = new[]
				{ 
					checkOperationalRadius,
					updatePosition,
					new AI.Task
					{
						Interval = 1.0f,
						Action = delegate()
						{
							raycastAI.Move(new Vector3(((float)this.random.NextDouble() * 2.0f) - 1.0f, ((float)this.random.NextDouble() * 2.0f) - 1.0f, ((float)this.random.NextDouble() * 2.0f) - 1.0f));
						}
					},
					new AI.Task
					{
						Interval = 1.0f,
						Action = delegate()
						{
							Agent a = Agent.Query(transform.Position, sightDistance, hearingDistance, x => x.Entity.Type == "Player");
							if (a != null)
								ai.CurrentState.Value = "Alert";
						},
					},
				},
			});

			ai.Add(new AI.AIState
			{
				Name = "Alert",
				Tasks = new[]
				{ 
					checkOperationalRadius,
					updatePosition,
					new AI.Task
					{
						Interval = 1.0f,
						Action = delegate()
						{
							if (ai.TimeInCurrentState > 3.0f)
								ai.CurrentState.Value = "Idle";
							else
							{
								Agent a = Agent.Query(transform.Position, sightDistance, hearingDistance, x => x.Entity.Type == "Player");
								if (a != null)
								{
									ai.TargetAgent.Value = a.Entity;
									ai.CurrentState.Value = "Chase";
								}
							}
						},
					},
				},
			});

			AI.Task checkTargetAgent = new AI.Task
			{
				Action = delegate()
				{
					Entity target = ai.TargetAgent.Value.Target;
					if (target == null || !target.Active)
					{
						ai.TargetAgent.Value = null;
						ai.CurrentState.Value = "Idle";
					}
				},
			};

			// Chase AI state

			ai.Add(new AI.AIState
			{
				Name = "Chase",
				Enter = delegate(AI.AIState previous)
				{
					AkSoundEngine.PostEvent(AK.EVENTS.EVIL_CUBES_CHASE, entity);
					raycastAI.BlendTime.Value = 0.5f;
				},
				Exit = delegate(AI.AIState next)
				{
					raycastAI.BlendTime.Value = 1.0f;
					AkSoundEngine.PostEvent(AK.EVENTS.EVIL_CUBES_IDLE, entity);
				},
				Tasks = new[]
				{
					checkOperationalRadius,
					checkTargetAgent,
					new AI.Task
					{
						Interval = 0.5f,
						Action = delegate()
						{
							raycastAI.Move(ai.TargetAgent.Value.Target.Get<Transform>().Position.Value - transform.Position);
						}
					},
					updatePosition,
				},
			});

			this.SetMain(entity, main);
		}
	}
}