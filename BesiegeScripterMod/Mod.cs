﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using spaar.ModLoader;
using UnityEngine;
using NLua;
using LenchScripterMod.Blocks;

namespace LenchScripterMod
{
    public class ScripterMod : Mod
    {
        public override string Name { get; } = "Lench Scripter Mod";
        public override string DisplayName { get; } = "Lench Scripter Mod";
        public override string Author { get; } = "Lench";
        public override Version Version { get; } = new Version(0, 9, 2);
        public override string VersionExtra { get; } = "";
        public override string BesiegeVersion { get; } = "v0.27";
        public override bool CanBeUnloaded { get; } = true;
        public override bool Preload { get; } = false;

        /// <summary>
        /// SingleInstance of scripter mod.
        /// </summary>
        public static Scripter scripter;

        internal static LuaWatchlist watchlist;
        internal static Type blockScriptType;

        /// <summary>
        /// Instantiates the mod and it's components.
        /// Looks for and loads assemblies.
        /// </summary>
        public override void OnLoad()
        {
            UnityEngine.Object.DontDestroyOnLoad(Scripter.Instance);
            scripter = Scripter.Instance;
            Game.OnSimulationToggle += scripter.OnSimulationToggle;

            watchlist = new LuaWatchlist();

            if (LoadBlockLoaderAssembly())
            {
                Debug.Log("[Lench Scripter Mod]: Found TGYD's BlockLoader");
            }

            addKeybinds();
        }

        /// <summary>
        /// Disables the mod from executing scripts.
        /// </summary>
        public override void OnUnload()
        {
            Game.OnSimulationToggle -= scripter.OnSimulationToggle;
            scripter.OnSimulationToggle(false);
        }

        /// <summary>
        /// Attempts to load TGYD's BlockLoader assembly.
        /// </summary>
        /// <returns>Returns true if successfull.</returns>
        private bool LoadBlockLoaderAssembly()
        {
            Assembly blockLoaderAssembly;
            try
            {
                blockLoaderAssembly = Assembly.LoadFrom(Application.dataPath + "/Mods/BlockLoader.dll");
            }
            catch (FileNotFoundException)
            {
                return false;
            }

            foreach (Type type in blockLoaderAssembly.GetExportedTypes())
            {
                if (type.FullName == "BlockScript")
                    blockScriptType = type;
            }

            if (blockScriptType == null)
                return false;

            return true;
        }

        private void addKeybinds()
        {
            Keybindings.AddKeybinding("Dump Blocks ID", new Key(KeyCode.None, KeyCode.LeftShift));
            Keybindings.AddKeybinding("Lua Watchlist", new Key(KeyCode.LeftControl, KeyCode.I));
        }
    }

    /// <summary>
    /// Class representing an instance of the mod.
    /// </summary>
    public class Scripter : SingleInstance<Scripter>
    {
        /// <summary>
        /// Name in the Unity hierarchy.
        /// </summary>
        public override string Name { get; } = "Lench Scripter";

        // Object passed to lua
        private static LuaMethodWrapper wrapper;

        // Lua environment
        internal Lua lua;
        private string scriptFile;

        // Lua functions
        private static LuaFunction luaOnUpdate;
        private static LuaFunction luaOnFixedUpdate;
        private static LuaFunction luaOnKeyDown;
        private static LuaFunction luaOnKeyHeld;

        internal bool isSimulating;

        // Hovered block for ID dumping
        private GenericBlock hoveredBlock;

        // Machine changed - flag for rebuild
        private bool rebuildDict = false;

        // Map: Building Block -> ID
        private Dictionary<GenericBlock, string> buildingBlocks;

        // Map: GUID -> Simulation Block
        private Dictionary<string, Block> guidToSimulationBlock;

        // Map: ID -> Simulation Block
        private Dictionary<string, Block> idToSimulationBlock;

        /// <summary>
        /// Initializes and returns new Block object.
        /// </summary>
        /// <param name="bb">BlockBehaviour object.</param>
        /// <returns>LenchScripterMod.Block object.</returns>
        internal Block CreateBlock(BlockBehaviour bb)
        {
            Block block;
            if (Cannon.isCannon(bb))
                block = gameObject.AddComponent<Cannon>();
            else if (Cog.isCog(bb))
                block = gameObject.AddComponent<Cog>();
            else if (Decoupler.isDecoupler(bb))
                block = gameObject.AddComponent<Decoupler>();
            else if (Flamethrower.isFlamethrower(bb))
                block = gameObject.AddComponent<Flamethrower>();
            else if (FlyingSpiral.isFlyingSpiral(bb))
                block = gameObject.AddComponent<FlyingSpiral>();
            else if (Grabber.isGrabber(bb))
                block = gameObject.AddComponent<Grabber>();
            else if (Grenade.isGrenade(bb))
                block = gameObject.AddComponent<Grenade>();
            else if (Piston.isPiston(bb))
                block = gameObject.AddComponent<Piston>();
            else if (Rocket.isRocket(bb))
                block = gameObject.AddComponent<Rocket>();
            else if (Spring.isSpring(bb))
                block = gameObject.AddComponent<Spring>();
            else if (Steering.isSteering(bb))
                block = gameObject.AddComponent<Steering>();
            else if (WaterCannon.isWaterCannon(bb))
                block = gameObject.AddComponent<WaterCannon>();
            else
                block = gameObject.AddComponent<Block>();
            block.Initialize(bb);
            block.enabled = true;
            return block;
        }

        /// <summary>
        /// Finds blockId string in dictionary of simulation blocks.
        /// On first call of the simulation, it also initializes the dictionary.
        /// </summary>
        /// <param name="blockId">Blocks unique identifier.</param>
        /// <returns>Returns reference to blocks BlockBehaviour object.</returns>
        internal Block GetBlock(string blockId)
        {
            if (idToSimulationBlock == null)
                InitializeSimulationBlockHandlers();

            if (idToSimulationBlock.ContainsKey(blockId.ToUpper()))
                return idToSimulationBlock[blockId.ToUpper()];
            if (guidToSimulationBlock.ContainsKey(blockId))
                return guidToSimulationBlock[blockId];
            throw new BlockNotFoundException("Block " + blockId + " not found.");
        }

        /// <summary>
        /// Populates dictionary with references to building blocks.
        /// Used for dumping block IDs while building.
        /// Called at first DumpBlockID after machine change.
        /// </summary>
        internal void InitializeBuildingBlockIDs()
        {
            var typeCount = new Dictionary<string, int>();
            if (buildingBlocks == null)
            {
                Game.OnBlockPlaced += (Transform block) => rebuildDict = true;
                Game.OnBlockRemoved += () => rebuildDict = true;
            }
            buildingBlocks = new Dictionary<GenericBlock, string>();
            for (int i = 0; i < Machine.Active().BuildingBlocks.Count; i++)
            {
                GenericBlock block = Machine.Active().BuildingBlocks[i].GetComponent<GenericBlock>();
                string name = Machine.Active().BuildingBlocks[i].GetComponent<MyBlockInfo>().blockName.ToUpper();
                typeCount[name] = typeCount.ContainsKey(name) ? typeCount[name] + 1 : 1;
                buildingBlocks[block] = name + " " + typeCount[name];
            }
            rebuildDict = false;
        }

        /// <summary>
        /// Populates dictionary with references to simulation blocks.
        /// Used for accessing blocks with GetBlock(blockId) while simulating.
        /// Called at the start of simulation.
        /// </summary>
        private void InitializeSimulationBlockHandlers()
        {
            idToSimulationBlock = new Dictionary<string, Block>();
            guidToSimulationBlock = new Dictionary<string, Block>();
            var typeCount = new Dictionary<string, int>();
            for (int i = 0; i < Machine.Active().BuildingBlocks.Count; i++)
            {
                string name = Machine.Active().BuildingBlocks[i].GetComponent<MyBlockInfo>().blockName.ToUpper();
                typeCount[name] = typeCount.ContainsKey(name) ? typeCount[name] + 1 : 1;
                string id = name + " " + typeCount[name];
                string guid = Machine.Active().BuildingBlocks[i].Guid.ToString();
                Block b = CreateBlock(Machine.Active().Blocks[i]);
                idToSimulationBlock[id] = b;
                guidToSimulationBlock[guid] = b;
            }
        }

        private void LoadLuaScript()
        {
            try
            {
                lua.DoFile(scriptFile);
                luaOnUpdate = lua["onUpdate"] as LuaFunction;
                luaOnFixedUpdate = lua["onFixedUpdate"] as LuaFunction;
                luaOnKeyDown = lua["onKeyDown"] as LuaFunction;
                luaOnKeyHeld = lua["onKeyHeld"] as LuaFunction;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                ScriptStop();
            }
        }

        /// <summary>
        /// Called to start script.
        /// </summary>
        private void ScriptStart()
        {
            idToSimulationBlock = null;
            luaOnUpdate = null;
            luaOnFixedUpdate = null;
            luaOnKeyDown = null;
            luaOnKeyHeld = null;

            // Lua Environment
            lua = new Lua();
            lua.LoadCLRPackage();
            lua.DoString(@" import 'System'
                            import 'UnityEngine' ");
            lua.DoString(@"package.path = package.path .. ';"+ Application.dataPath + "/Scripts/?.lua'");

            wrapper = new LuaMethodWrapper();
            lua["besiege"] = wrapper;

            // Populate keycode table
            lua.NewTable("KeyCode");
            foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
            {
                string str = key.ToString();
                object[] objArray = new object[] { "KeyCode[\"", str, "\"] = ", (int)key };
                lua.DoString(string.Concat(objArray), "chunk");
            }

            // Find script file
            string luaFile = string.Concat(Application.dataPath, "/Scripts/", MyTextField.lastNameUsed, ".lua");
            if (File.Exists(luaFile))
            {
                Debug.Log("Script file: " + luaFile);
                scriptFile = luaFile;
            }
            else
            {
                Debug.Log("Script file not found: " + luaFile);
                ScriptStop();
            }
        }

        /// <summary>
        /// Called to stop script.
        /// </summary>
        private void ScriptStop()
        {
            lua.Close();
            lua.Dispose();
            lua = null;
            wrapper.clearMarks();
            wrapper = null;
            foreach (Block b in gameObject.GetComponents<Block>())
            {
                Destroy(b);
            }
            Debug.Log("Script stopped");
        }

        /// <summary>
        /// Finds hovered block in buildingBlocks dictionary and dumps its ID string
        /// if LeftShift is pressed.
        /// </summary>
        private void DumpHoveredBlock()
        {
            if (Game.AddPiece.HoveredBlock == null)
            {
                hoveredBlock = null;
                return;
            }

            if (hoveredBlock != null && Game.AddPiece.HoveredBlock == hoveredBlock)
                return;

            hoveredBlock = Game.AddPiece.HoveredBlock;

            if (rebuildDict || buildingBlocks == null)
                InitializeBuildingBlockIDs();

            string key;
            try
            {
                key = buildingBlocks[hoveredBlock];
            }
            catch (KeyNotFoundException)
            {
                InitializeBuildingBlockIDs();
                key = buildingBlocks[hoveredBlock];
            }
            string guid = hoveredBlock.GetComponent<BlockBehaviour>().Guid.ToString();
            Debug.Log(key + "  -  " + guid);
        }

        /// <summary>
        /// Mod functionality.
        /// Calls Lua functions.
        /// </summary>
        private void Update()
        {
            // Initialize Lua script
            if (scriptFile != null)
            {
                LoadLuaScript();
                scriptFile = null;
            }

            // Toggle watchlist visibility
            if (Keybindings.Get("Lua Watchlist").Pressed())
            {
                ScripterMod.watchlist.visible = !ScripterMod.watchlist.visible;
            }
                
            if (!isSimulating)
            {
                // Dump block identifiers
                if (Keybindings.Get("Dump Blocks ID").IsDown())
                {
                    DumpHoveredBlock();
                }
            }

            if (!isSimulating) return;

            if (lua == null) return;

            // Call Lua onUpdate
            if (luaOnUpdate != null)
                luaOnUpdate.Call();

            // Call Lua onKey
            if (Input.anyKey)
            {
                if ((string)lua.DoString("return type(onKeyHeld)", "chunk")[0] == "function")
                {
                    foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
                    {
                        if (!Input.GetKey(key)) continue;
                        if (luaOnKeyHeld != null)
                            luaOnKeyHeld.Call((int)key);
                    }
                }

                if ((string)lua.DoString("return type(onKeyDown)", "chunk")[0] == "function")
                {
                    foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
                    {
                        if (!Input.GetKeyDown(key)) continue;
                        if (luaOnKeyDown != null)
                            luaOnKeyDown.Call((int)key);
                    }
                }
            }
        }

        /// <summary>
        /// Calls Lua functions at a fixed rate.
        /// </summary>
        private void FixedUpdate()
        {
            // Initialize Lua script
            if (scriptFile != null)
            {
                LoadLuaScript();
                scriptFile = null;
            }

            if (!isSimulating) return;

            if (lua == null) return;

            // Call Lua onFixedUpdate
            if (luaOnFixedUpdate != null)
                luaOnFixedUpdate.Call();
        }

        /// <summary>
        /// Calls the editor GUI to render.
        /// </summary>
        private void OnGUI()
        {
            ScripterMod.watchlist.Render();
        }

        /// <summary>
        /// Handles starting and stopping of the simulation.
        /// </summary>
        /// <param name="isSimulating"></param>
        internal void OnSimulationToggle(bool isSimulating)
        {
            this.isSimulating = isSimulating;
            if (isSimulating)
                ScriptStart();
            else if (lua != null)
                ScriptStop();
        }
    }

}
