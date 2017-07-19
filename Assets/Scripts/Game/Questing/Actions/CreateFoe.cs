﻿// Project:         Daggerfall Tools For Unity
// Copyright:       Copyright (C) 2009-2017 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: Gavin Clayton (interkarma@dfworkshop.net)
// Contributors:    
// 
// Notes:
//

using UnityEngine;
using System;
using System.Text.RegularExpressions;
using DaggerfallWorkshop.Utility;

namespace DaggerfallWorkshop.Game.Questing
{
    /// <summary>
    /// Spawn a Foe resource into the world.
    /// </summary>
    public class CreateFoe : ActionTemplate
    {
        Symbol foeSymbol;
        uint spawnInterval;
        int spawnMaxTimes;
        int spawnChance;

        ulong lastSpawnTime = 0;
        int spawnCounter = 0;

        bool spawnInProgress = false;
        GameObject[] pendingFoeGameObjects;
        int pendingFoesSpawned;

        public override string Pattern
        {
            get
            {
                return @"create foe (?<symbol>[a-zA-Z0-9_.-]+) every (?<minutes>\d+) minutes (?<infinite>indefinitely) with (?<percent>\d+)% success|" +
                       @"create foe (?<symbol>[a-zA-Z0-9_.-]+) every (?<minutes>\d+) minutes (?<count>\d+) times with (?<percent>\d+)% success";
            }
        }

        public CreateFoe(Quest parentQuest)
            : base(parentQuest)
        {
            PlayerEnterExit.OnTransitionDungeonExterior += PlayerEnterExit_OnTransitionExterior;
            PlayerEnterExit.OnTransitionExterior += PlayerEnterExit_OnTransitionExterior;
            StreamingWorld.OnInitWorld += StreamingWorld_OnInitWorld;
        }

        public override void InitialiseOnSet()
        {
            lastSpawnTime = 0;
            spawnCounter = 0;
        }

        public override IQuestAction CreateNew(string source, Quest parentQuest)
        {
            base.CreateNew(source, parentQuest);

            // Source must match pattern
            Match match = Test(source);
            if (!match.Success)
                return null;

            // Factory new action
            CreateFoe action = new CreateFoe(parentQuest);
            action.foeSymbol = new Symbol(match.Groups["symbol"].Value);
            action.spawnInterval = (uint)Parser.ParseInt(match.Groups["minutes"].Value) * 60;
            action.spawnMaxTimes = Parser.ParseInt(match.Groups["count"].Value);
            action.spawnChance = Parser.ParseInt(match.Groups["percent"].Value);

            // Handle infinite
            if (!string.IsNullOrEmpty(match.Groups["infinite"].Value))
                action.spawnMaxTimes = -1;

            return action;
        }

        public override void Update(Task caller)
        {
            // Do nothing if max foes already spawned
            // This can be cleared on next set/rearm
            if (spawnCounter >= spawnMaxTimes && spawnMaxTimes != -1)
                return;

            // Clear pending foes if all have been spawned
            if (spawnInProgress && pendingFoesSpawned >= pendingFoeGameObjects.Length)
            {
                spawnInProgress = false;
                spawnCounter++;
                return;
            }

            // Check for a new spawn event - only one spawn event can be running at a time
            ulong gameSeconds = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToSeconds();
            if (gameSeconds > lastSpawnTime + spawnInterval && !spawnInProgress)
            {
                // Update last spawn time
                lastSpawnTime = gameSeconds;

                // Roll for spawn chance
                float chance = spawnChance / 100f;
                if (UnityEngine.Random.Range(0f, 1f) > chance)
                    return;

                // Start deploying GameObjects
                CreatePendingFoeSpawn();
            }

            // Try to deploy a pending spawns
            if (spawnInProgress)
            {
                TryPlacement();
            }
        }

        #region Private Methods

        void CreatePendingFoeSpawn()
        {
            // Get the Foe resource
            Foe foe = ParentQuest.GetFoe(foeSymbol);
            if (foe == null)
                throw new Exception(string.Format("create foe could not find Foe with symbol name {0}", Symbol.Name));

            // Get foe GameObjects
            pendingFoeGameObjects = foe.CreateFoeGameObjects(Vector3.zero);
            if (pendingFoeGameObjects == null || pendingFoeGameObjects.Length != foe.SpawnCount)
                throw new Exception(string.Format("create foe attempted to create {0}x{1} GameObjects and failed.", foe.SpawnCount, Symbol.Name));

            // Initiate deployment process
            // Usually the foe will spawn immediately but can take longer depending on available placement space
            // This process ensures these foes have all been deployed before starting next cycle
            spawnInProgress = true;
            pendingFoesSpawned = 0;
        }

        void TryPlacement()
        {
            // Place in world near player depending on local area
            PlayerEnterExit playerEnterExit = GameManager.Instance.PlayerEnterExit;
            if (playerEnterExit.IsPlayerInsideBuilding)
            {
                PlaceFoeBuildingInterior(pendingFoeGameObjects, playerEnterExit.Interior);
            }
            else if (playerEnterExit.IsPlayerInsideDungeon)
            {
                PlaceFoeDungeonInterior(pendingFoeGameObjects, playerEnterExit.Dungeon);
            }
            else if (!playerEnterExit.IsPlayerInside && GameManager.Instance.PlayerGPS.IsPlayerInLocationRect)
            {
                PlaceFoeExteriorLocation(pendingFoeGameObjects, GameManager.Instance.StreamingWorld.CurrentPlayerLocationObject);
            }
            else
            {
                PlaceFoeWilderness(pendingFoeGameObjects);
            }
        }

        #endregion

        #region Placement Methods

        // Place foe somewhere near player when inside a building
        // Building interiors have spawn nodes for this placement so we can roll out foes all at once
        void PlaceFoeBuildingInterior(GameObject[] gameObjects, DaggerfallInterior interiorParent)
        {
            // Must have a DaggerfallLocation parent
            if (interiorParent == null)
                throw new Exception("PlaceFoeFreely() must have a DaggerfallLocation parent object.");

            // Use free spawn if no interior spawn points
            if (interiorParent.SpawnPoints.Length == 0)
            {
                PlaceFoeFreely(gameObjects, interiorParent.transform);
                return;
            }

            // Place using spawn points
            foreach (GameObject go in gameObjects)
            {
                Vector3 spawnPosition;
                bool spawnPositionFound = interiorParent.GetRandomSpawnPoint(out spawnPosition);
                go.transform.parent = interiorParent.transform;

                if (spawnPositionFound)
                {
                    go.transform.localPosition = spawnPosition;
                }
                else
                {
                    Debug.Log("Fallback placing behind player");
                    PlayerMotor playerMotor = GameManager.Instance.PlayerMotor;
                    go.transform.position = playerMotor.transform.position + -playerMotor.transform.forward;
                }

                FinalizeFoe(go);
                pendingFoesSpawned++;
            }
        }

        // Place foe somewhere near player when inside a dungeon
        // Dungeons interiors are complex 3D environments with no navgrid/navmesh or known spawn nodes
        void PlaceFoeDungeonInterior(GameObject[] gameObjects, DaggerfallDungeon dungeonParent)
        {
            PlaceFoeFreely(gameObjects, dungeonParent.transform);
        }

        // Place foe somewhere near player when outside a location navgrid is available
        // Navgrid placement helps foe avoid getting tangled in geometry like buildings
        void PlaceFoeExteriorLocation(GameObject[] gameObjects, DaggerfallLocation locationParent)
        {
            PlaceFoeFreely(gameObjects, locationParent.transform);
        }

        // Place foe somewhere near player when outside and no navgrid available
        // Wilderness environments are currently open so can be placed on ground anywhere within range
        void PlaceFoeWilderness(GameObject[] gameObjects)
        {
            GameManager.Instance.StreamingWorld.TrackLooseObject(gameObjects[pendingFoesSpawned], -1, -1, true);
            PlaceFoeFreely(gameObjects, null, 8f, 25f);
        }

        // Uses raycasts to find next spawn position just outside of player's field of view
        void PlaceFoeFreely(GameObject[] gameObjects, Transform parent, float minDistance = 5f, float maxDistance = 20f)
        {
            const float overlapSphereRadius = 0.65f;
            const float separationDistance = 1.25f;
            const float maxFloorDistance = 4f;

            // Must have received a valid array
            if (gameObjects == null || gameObjects.Length == 0)
                return;

            // Set parent - otherwise caller must set a parent
            if (parent)
                gameObjects[pendingFoesSpawned].transform.parent = parent;

            // Select a left or right direction outside of camera FOV
            Quaternion rotation;
            float directionAngle = GameManager.Instance.MainCamera.fieldOfView;
            directionAngle += UnityEngine.Random.Range(0f, 4f);
            if (UnityEngine.Random.Range(0f, 1f) > 0.5f)
                rotation = Quaternion.Euler(0, -directionAngle, 0);
            else
                rotation = Quaternion.Euler(0, directionAngle, 0);

            // Get direction vector and create a new ray
            Vector3 angle = (rotation * Vector3.forward).normalized;
            Vector3 spawnDirection = GameManager.Instance.PlayerObject.transform.TransformDirection(angle).normalized;
            Ray ray = new Ray(GameManager.Instance.PlayerObject.transform.position, spawnDirection);

            // Check for a hit
            Vector3 currentPoint;
            RaycastHit initialHit;
            if (Physics.Raycast(ray, out initialHit, maxDistance))
            {
                // Separate out from hit point
                float extraDistance = UnityEngine.Random.Range(0f, 2f);
                currentPoint = initialHit.point + initialHit.normal.normalized * (separationDistance + extraDistance);

                // Must be greater than minDistance
                if (initialHit.distance < minDistance)
                    return;
            }
            else
            {
                // Player might be in an open area (e.g. outdoors) pick a random point along spawn direction
                currentPoint = GameManager.Instance.PlayerObject.transform.position + spawnDirection * UnityEngine.Random.Range(minDistance, maxDistance);
            }

            // Must be able to find a surface below
            RaycastHit floorHit;
            ray = new Ray(currentPoint, Vector3.down);
            if (!Physics.Raycast(ray, out floorHit, maxFloorDistance))
                return;

            // Ensure this is open space
            Vector3 testPoint = floorHit.point + Vector3.up * separationDistance;
            Collider[] colliders = Physics.OverlapSphere(testPoint, overlapSphereRadius);
            if (colliders.Length > 0)
                return;

            // This looks like a good spawn position
            pendingFoeGameObjects[pendingFoesSpawned].transform.position = testPoint;
            FinalizeFoe(pendingFoeGameObjects[pendingFoesSpawned]);
            gameObjects[pendingFoesSpawned].transform.LookAt(GameManager.Instance.PlayerObject.transform.position);

            // Increment count
            pendingFoesSpawned++;
        }

        // Fine tunes foe position slightly based on mobility and enables GameObject
        void FinalizeFoe(GameObject go)
        {
            DaggerfallMobileUnit mobileUnit = go.GetComponentInChildren<DaggerfallMobileUnit>();
            if (mobileUnit)
            {
                // Align ground creatures on surface, raise flying creatures slightly into air
                if (mobileUnit.Summary.Enemy.Behaviour != MobileBehaviour.Flying)
                    GameObjectHelper.AlignControllerToGround(go.GetComponent<CharacterController>());
                else
                    go.transform.localPosition += Vector3.up * 1.5f;
            }
            else
            {
                // Just align to ground
                GameObjectHelper.AlignControllerToGround(go.GetComponent<CharacterController>());
            }

            go.SetActive(true);
        }

        #endregion

        #region Event Handlers

        private void PlayerEnterExit_OnTransitionExterior(PlayerEnterExit.TransitionEventArgs args)
        {
            // Any foes pending placement to dungeon or building interior are now invalid
            pendingFoeGameObjects = null;
            spawnInProgress = false;
        }

        private void StreamingWorld_OnInitWorld()
        {
            // Any foes pending placement to loose objects container are now invalid
            pendingFoeGameObjects = null;
            spawnInProgress = false;
        }

        #endregion
    }
}