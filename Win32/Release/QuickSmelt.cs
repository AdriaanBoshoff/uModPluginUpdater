using Facepunch.Extend;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("QuickSmelt", "Wulf/Jake-Rich", "4.0.5", ResourceId = 1067)]
    [Description("Increases the speed of the furnace smelting")]

    class QuickSmelt : RustPlugin
    {
        public static QuickSmelt _plugin;
        public static JSONFile<ConfigData> _settingsFile;
        public static ConfigData Settings { get { return _settingsFile.Instance; } }
        public static MethodInfo ConsumeFuelMethod;

        public const string permAllow = "quicksmelt.allow";

        public class ConfigData
        {
            public int SmeltSpeed = 1;
            public int WoodRate = 1;
            public float CharcoalRate = 0.70f;
            public bool CanCookFoodInFurnace = false;
            public bool UsePermissions = false;

            [JsonProperty(PropertyName = "Large Furnace Multiplier")]
            public float LargeFurnaceMultiplier = 1.0f;
            [JsonProperty(PropertyName = "Campfire Multiplier")]
            public float CampFireMultiplier = 1.0f;
            [JsonProperty(PropertyName = "Oil Refinery Multiplier")]
            public float OilRefineryMultiplier = 1.0f;
            [JsonProperty(PropertyName = "Water Purifier Multiplier")]
            public float WaterPurifierMultiplier = 1.0f;
            //public float Efficency = 1f;


            public int GetSmeltRate(BaseEntity entity)
            {
                switch (entity.ShortPrefabName)
                {
                    default: { return SmeltSpeed; }
                    case "furnace_static": { return (int)(SmeltSpeed * LargeFurnaceMultiplier); }
                    case "hobobarrel_static": { return (int)(SmeltSpeed * CampFireMultiplier); }
                    case "furnace": { return SmeltSpeed; }
                    case "BBQ.Deployed": { return (int)(SmeltSpeed * CampFireMultiplier); }
                    case "campfire_static": { return (int)(SmeltSpeed * CampFireMultiplier); }
                    case "campfire": { return (int)(SmeltSpeed * CampFireMultiplier); }
                    case "small_refinery_static": { return (int)(SmeltSpeed * OilRefineryMultiplier); }
                    case "refinery_small_deployed": { return (int)(SmeltSpeed * OilRefineryMultiplier); }
                    case "fireplace.deployed": { return (int)(SmeltSpeed * CampFireMultiplier); }
                }
            }
        }

        public List<string> FurnaceTypes = new List<string>();

        private static HashSet<string> RawMeatNames = new HashSet<string>()
        {
            "bearmeat",
            "meat.boar",
            "wolfmeat.raw",
            "humanmeat.raw",
            "fish.raw",
            "chicken.raw",
            "deermeat.raw",
            "horsemeat.raw",
        };

        void Init()
        {
            _plugin = this;
            permission.RegisterPermission(permAllow, this);
        }
		
        void Loaded()
        {
            _settingsFile = new JSONFile<ConfigData>("QuickSmelt", ConfigLocation.Config, extension: ".json");
            ConsumeFuelMethod = typeof(BaseOven).GetMethod("ConsumeFuel", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }

		void Unload()
		{
            //Make sure to always return things to their vanilla state
		    foreach(var oven in BaseNetworkable.serverEntities.OfType<BaseOven>())
            {
                oven.allowByproductCreation = true;

                var data = oven.GetComponent<FurnaceData>();

                if (oven.IsOn())
                {
                    //Stop the modded smelting, resume the vanilla smelting
                    StopCooking(oven);
                    oven.StartCooking();
                }

                //Get rid of those monobehaviors
                UnityEngine.Object.Destroy(data);
            }
		}

		void OnServerInitialized()
        {
		    foreach(var oven in BaseNetworkable.serverEntities.OfType<BaseOven>().Where(x=>x.IsOn()))
            {
                NextFrame(() =>
                {
                    if (oven == null || oven.IsDestroyed)
                    {
                        return;
                    }
                    //So invokes are actually removed at the end of a frame, meaning you can get multiple invokes at once after reloading plugin
                    StopCooking(oven);
                    StartCooking(oven);
                });
            }
        }

		object OnOvenToggle(BaseOven oven, BasePlayer player)
		{
            if (oven is BaseFuelLightSource || (oven.needsBuildingPrivilegeToUse && !player.CanBuild()))
            {
                return null;
            }
			if (!oven.HasFlag(BaseEntity.Flags.On))
			{
				StartCooking(oven);
			}
            else
            {
                StopCooking(oven);
            }
            return false;
		}
		
        public Item FindBurnable(BaseOven oven)
        {
            return oven.inventory.itemList.FirstOrDefault(x => //TIL you can declare Linq over multiple lines
            {
                var comp = x.info.GetComponent<ItemModBurnable>();
                if (comp != null && (oven.fuelType == null || x.info == oven.fuelType))
                {
                    return true;
                }
                return false;
            });
        }

        //Overwriting Oven.StartCooking
		void StartCooking(BaseOven oven)
		{
            if ((Settings.UsePermissions && !permission.UserHasPermission(oven.OwnerID.ToString(), permAllow)))
            {
                oven.StartCooking();
                return;
            }
            if (FindBurnable(oven) == null)
            {
                return;
            }
            oven.UpdateAttachmentTemperature();
            var data = oven.transform.GetOrAddComponent<FurnaceData>();
            oven.CancelInvoke(oven.Cook);
            oven.InvokeRepeating(data.CookOverride, 0.5f, 0.5f);
            oven.SetFlag(BaseEntity.Flags.On, true, false);
        }
		
        void StopCooking(BaseOven oven)
        {
            var data = oven.transform.GetOrAddComponent<FurnaceData>();
            oven.CancelInvoke(data.CookOverride);
            oven.StopCooking();
        }

		void OnConsumeFuel(BaseOven oven, Item fuel, ItemModBurnable burnable)
        {
            BaseEntity slot = oven.GetSlot(BaseEntity.Slot.FireMod);
            if (slot != null)
            {
                //Usually 0.5f for cook tick, fuel is only consumed twice per second
                slot.SendMessage("Cook", 2f * Settings.SmeltSpeed * Settings.WaterPurifierMultiplier, SendMessageOptions.DontRequireReceiver);
            }

            if (oven == null || oven is BaseFuelLightSource)
            {
                return;
            }

            // Check if permissions are enabled and player has permission
            if (Settings.UsePermissions && !permission.UserHasPermission(oven.OwnerID.ToString(), permAllow)) return;

            var data = oven.transform.GetOrAddComponent<FurnaceData>();

            #region Charcoal Modifier

            if (burnable.byproductItem != null)
            {
                oven.allowByproductCreation = false;

                int charcoalAmount = 0;

                float modifiedRate = Settings.CharcoalRate * Settings.WoodRate;
                
                charcoalAmount += (int)(Settings.CharcoalRate * Settings.WoodRate);

                modifiedRate -= charcoalAmount;

                if (modifiedRate > 0 && modifiedRate <= 1f)
                {
                    if (UnityEngine.Random.Range(0f, 1f) < modifiedRate)
                    {
                        charcoalAmount += 1;
                    }
                }

                if (charcoalAmount > 0)
                {
                    TryAddItem(oven.inventory, burnable.byproductItem, Mathf.Min(charcoalAmount, fuel.amount));
                }
            }

            #endregion

            // Modify the amount of fuel to use
            fuel.UseItem(Settings.WoodRate - 1);
        }

        public static int TakeFromInventorySlot(ItemContainer container, int itemId, int amount, Item item)
        {
            if (item.info.itemid != itemId) return 0;

            if (item.amount > amount)
            {
                item.UseItem(amount);
                return amount;
            }

            amount = item.amount;
            item.Remove();
            return amount;
        }

        public static void TryAddItem(ItemContainer container, ItemDefinition definition, int amount)
        {
            int amountLeft = amount;
            foreach (var item in container.itemList)
            {
                if (item.info != definition)
                {
                    continue;
                }
                if (amountLeft <= 0)
                {
                    return;
                }
                if (item.amount < item.MaxStackable())
                {
                    int amountToAdd = Mathf.Min(amountLeft, item.MaxStackable() - item.amount);
                    item.amount += amountToAdd;
                    item.MarkDirty();
                    amountLeft -= amountToAdd;
                }
            }
            if (amountLeft <= 0)
            {
                return;
            }
            var smeltedItem = ItemManager.Create(definition, amountLeft);
            if (!smeltedItem.MoveToContainer(container))
            {
                smeltedItem.Drop(container.dropPosition, container.dropVelocity);
                var oven = container.entityOwner as BaseOven;
                if (oven != null)
                {
                    _plugin.StopCooking(oven);
                    //oven.OvenFull();
                }
            }
        }

        public class FurnaceData : MonoBehaviour
        {
            private BaseOven _oven;
            public BaseOven Furnace { get { if (_oven == null) { _oven = GetComponent<BaseOven>(); } return _oven; } } //One line bullshit right here

            public int SmeltTicks = 0;
            public Dictionary<string, float> ItemLeftovers = new Dictionary<string, float>();

            public void CookOverride()
            {
                SmeltTicks++;
                if (SmeltTicks % 2 == 0)
                {
                    TrySmeltItems();
                }
                var burnable = _plugin.FindBurnable(Furnace);
                if (burnable == null)
                {
                    _plugin.StopCooking(Furnace);
                    return;
                }
                ItemModBurnable component = burnable.info.GetComponent<ItemModBurnable>();
                burnable.fuel -= 0.5f * Furnace.cookingTemperature / 200f;
                if (!burnable.HasFlag(global::Item.Flag.OnFire))
                {
                    burnable.SetFlag(global::Item.Flag.OnFire, true);
                    burnable.MarkDirty();
                }
                if (burnable.fuel <= 0f)
                {
                    var array = ArrayPool.Get(2);
                    array[0] = burnable;
                    array[1] = component;
                    ConsumeFuelMethod.Invoke(Furnace, array);
                    ArrayPool.Free(array);
                }
            }

            void TrySmeltItems()
            {
                #region Smelt Modifier

                int smeltLoops = Mathf.Max(1, Settings.GetSmeltRate(Furnace));

                if (smeltLoops > 0)
                {
                    //Took from QuickSmelt and modified
                    // Loop through furance inventory slots
                    for (var i = 0; i < Furnace.inventory.itemList.Count; i++)
                    {
                        // Check for and ignore invalid items
                        var slotItem = Furnace.inventory.itemList[i];
                        if (slotItem == null || !slotItem.IsValid())
                        {
                            continue;
                        }

                        // Check for and ignore non-cookables
                        var cookable = slotItem.info.GetComponent<ItemModCookable>();
                        if (cookable == null)
                        {
                            continue;
                        }

                        //Make sure oil refinery only cooks oil, fireplace cooks food, furnace cooks ore
                        if (cookable.lowTemp > Furnace.cookingTemperature || cookable.highTemp < Furnace.cookingTemperature)
                        {
                            if (Settings.CanCookFoodInFurnace == true)
                            {
                                //Allow food to be cooked in furnaces
                                if (!RawMeatNames.Contains(slotItem.info.shortname))
                                {
                                    continue;
                                }
                            }
                            else
                            {
                                continue;
                            }
                        }

                        //_plugin.Puts($"{SmeltTicks} {(int)(cookable.cookTime)}");

                        //We do probability over time to see if we should cook instead of keeping track of item smelt time
                        //Will change this back to linear smelting once we expose ItemModCookable.OnCycle()
                        if ((int)cookable.cookTime != 0 && (SmeltTicks / 2) % (int)(cookable.cookTime) != 0)
                        {
                            continue;
                        }

                        // Skip already cooked food items
                        if (slotItem.info.shortname.EndsWith(".cooked"))
                        {
                            continue;
                        }

                        // Set consumption to however many we can pull from this actual stack
                        var consumptionAmount = TakeFromInventorySlot(Furnace.inventory, slotItem.info.itemid, Settings.SmeltSpeed, slotItem);

                        // If we took nothing, then... we can't create any
                        if (consumptionAmount <= 0)
                        {
                            continue;
                        }

                        //Will be used for efficency
                        int extraLoops = 1;

                        // Create the item(s) that are now cooked
                        TryAddItem(Furnace.inventory, cookable.becomeOnCooked, (cookable.amountOfBecome * consumptionAmount * extraLoops));
                    }
                }

                #endregion

                ItemManager.DoRemoves();
            }

            void Destroy()
            { 
                Furnace.CancelInvoke(CookOverride);
            }
        }

        [ConsoleCommand("quicksmelt.smelt")]
        void ChangeSmeltSpeed_ConsoleCommand(ConsoleSystem.Arg args)
        {
            if (!args.IsAdmin)
            {
                return;
            }
            if (args.HasArgs(1))
            {
                Settings.SmeltSpeed = args.GetInt(0, 1);
            }
            args.ReplyWith($"Smelt Speed: {Settings.SmeltSpeed}x (Default: 1)");
            _settingsFile.Save();
        }

        [ConsoleCommand("quicksmelt.wood")]
        void ChangeWoodRate_ConsoleCommand(ConsoleSystem.Arg args)
        {
            if (!args.IsAdmin)
            {
                return;
            }
            if (args.HasArgs(1))
            {
                Settings.WoodRate = args.GetInt(0, 1);
            }
            args.ReplyWith($"Wood Rate: {Settings.WoodRate} (Default: 1)");
            _settingsFile.Save();
        }

        [ConsoleCommand("quicksmelt.charcoal")]
        void ChangeCharcoal_ConsoleCommand(ConsoleSystem.Arg args)
        {
            if (!args.IsAdmin)
            {
                return;
            }
            if (args.HasArgs(1))
            {
                Settings.CharcoalRate = args.GetFloat(0, 1);
            }
            args.ReplyWith($"Charcoal Rate: {Settings.CharcoalRate.ToString("0.0")} (Default: 0.7)");
            _settingsFile.Save();
        }

        [ConsoleCommand("quicksmelt.food")]
        void ChangeFood_ConsoleCommand(ConsoleSystem.Arg args)
        {
            if (!args.IsAdmin)
            {
                return;
            }
            Settings.CanCookFoodInFurnace = !Settings.CanCookFoodInFurnace;
            args.ReplyWith($"Food will now {(Settings.CanCookFoodInFurnace ? "" : "not ")}cook in Furnaces.");
            _settingsFile.Save();
        }

        [ConsoleCommand("quicksmelt")]
        void QuickSmeltInfoCommand(ConsoleSystem.Arg args)
        {
            if (!args.IsAdmin)
            {
                return;
            }
            TextTable table = new TextTable();
            table.AddColumns("Description", "Setting", "Console Command");
            table.AddRow("", "");
            table.AddRow($"Smelt Speed", $"{Settings.SmeltSpeed}x", "quicksmelt.smelt");
            table.AddRow($"Charcoal Rate", $"{Settings.CharcoalRate.ToString("0.0")}x", "quicksmelt.charcoal");
            table.AddRow($"Wood Rate", $"{Settings.WoodRate}x", "quicksmelt.wood");
            table.AddRow($"Will Food Cook In Furnace", $"{Settings.CanCookFoodInFurnace}", "quicksmelt.food");
            args.ReplyWith(table.ToString());
        }

        #region Configuration Files

        public enum ConfigLocation
        {
            Data = 0,
            Config = 1,
            Logs = 2,
            Plugins = 3,
            Lang = 4,
            Custom = 5,
        }

        public class JSONFile<Type> where Type : class
        {
            private DynamicConfigFile _file;
            public string _name { get; set; }
            public Type Instance { get; set; }
            private ConfigLocation _location { get; set; }
            private string _path { get; set; }

            public JSONFile(string name, ConfigLocation location = ConfigLocation.Data, string path = null, string extension = ".json")
            {
                _name = name.Replace(".json", "");
                _location = location;
                switch (location)
                {
                    case ConfigLocation.Data:
                        {
                            _path = $"{Oxide.Core.Interface.Oxide.DataDirectory}/{name}{extension}";
                            break;
                        }
                    case ConfigLocation.Config:
                        {
                            _path = $"{Oxide.Core.Interface.Oxide.ConfigDirectory}/{name}{extension}";
                            break;
                        }
                    case ConfigLocation.Logs:
                        {
                            _path = $"{Oxide.Core.Interface.Oxide.LogDirectory}/{name}{extension}";
                            break;
                        }
                    case ConfigLocation.Lang:
                        {
                            _path = $"{Oxide.Core.Interface.Oxide.LangDirectory}/{name}{extension}";
                            break;
                        }
                    case ConfigLocation.Custom:
                        {
                            _path = $"{path}/{name}{extension}";
                            break;
                        }
                }
                _file = new DynamicConfigFile(_path);
                _file.Settings = new JsonSerializerSettings() { ReferenceLoopHandling = ReferenceLoopHandling.Ignore };
                Init();
            }

            public virtual void Init()
            {
                Load();
                Save();
                Load();
            }

            public virtual void Load()
            {

                if (!_file.Exists())
                {
                    Save();
                }
                Instance = _file.ReadObject<Type>();
                if (Instance == null)
                {
                    Instance = Activator.CreateInstance<Type>();
                    Save();
                }
                return;
            }

            public virtual void Save()
            {
                _file.WriteObject(Instance);
                return;
            }

            public virtual void Reload()
            {
                Load();
            }
        }

        #endregion
    }
}
