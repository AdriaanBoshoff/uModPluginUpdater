//Reference: Facepunch.Sqlite
//Reference: UnityEngine.UnityWebRequestModule
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace Oxide.Plugins
{
    [Info("Image Library", "Absolut & K1lly0u", "2.0.42", ResourceId = 2193)]
    [Description("Plugin API for downloading and managing images")]
    class ImageLibrary : RustPlugin
    {
        #region Fields

        private ImageIdentifiers imageIdentifiers;
        private ImageURLs imageUrls;
        private SkinInformation skinInformation;
        private DynamicConfigFile identifiers;
        private DynamicConfigFile urls;
        private DynamicConfigFile skininfo;

        private static ImageLibrary il;
        private ImageAssets assets;

        private Queue<LoadOrder> loadOrders = new Queue<LoadOrder>();
        private bool orderPending;
        private bool isInitialized;

        private readonly Regex avatarFilter = new Regex(@"<avatarFull><!\[CDATA\[(.*)\]\]></avatarFull>");

        #endregion Fields

        #region Oxide Hooks

        private void Loaded()
        {
            identifiers = Interface.Oxide.DataFileSystem.GetFile("ImageLibrary/image_data");
            urls = Interface.Oxide.DataFileSystem.GetFile("ImageLibrary/image_urls");
            skininfo = Interface.Oxide.DataFileSystem.GetFile("ImageLibrary/skin_data");
        }

        private void OnServerInitialized()
        {
            il = this;
            LoadVariables();
            LoadData();

            foreach (ItemDefinition item in ItemManager.itemList)
            {
                string workshopName = item.displayName.english.ToLower().Replace("skin", "").Replace(" ", "").Replace("-", "");
                if (!workshopNameToShortname.ContainsKey(workshopName))
                    workshopNameToShortname.Add(workshopName, item.shortname);
            }

            CheckForRefresh();

            foreach (var player in BasePlayer.activePlayerList)
                OnPlayerInit(player);
        }

        private void OnPlayerInit(BasePlayer player) => GetPlayerAvatar(player?.UserIDString);

        private void Unload()
        {
            SaveData();
            UnityEngine.Object.Destroy(assets);
            il = null;
        }

        #endregion Oxide Hooks

        #region Functions

        private void GetItemSkins()
        {
            PrintWarning("Retrieving item skin lists...");
            webrequest.Enqueue("http://s3.amazonaws.com/s3.playrust.com/icons/inventory/rust/schema.json", null, (code, response) =>
            {
                if (!(response == null && code == 200))
                {
                    Rust.Workshop.ItemSchema.Item[] items = JsonConvert.DeserializeObject<Rust.Workshop.ItemSchema>(response).items;

                    PrintWarning($"Found {items.Length} item skins. Gathering image URLs");
                    foreach (var item in items)
                    {
                        if (!string.IsNullOrEmpty(item.itemshortname) && !string.IsNullOrEmpty(item.icon_url))
                        {
                            string identifier;
                            ItemDefinition def = ItemManager.FindItemDefinition(item.itemshortname);
                            if (def == null)
                                continue;

                            int skinCount = def.skins.Count(k => k.id == item.itemdefid);
                            if (skinCount == 0)
                                identifier = $"{item.itemshortname}_{item.workshopid}";
                            else identifier = $"{item.itemshortname}_{item.itemdefid}";
                            if (!imageUrls.URLs.ContainsKey(identifier))
                                imageUrls.URLs.Add(identifier, item.icon_url);

                            skinInformation.skinData[identifier] = new Dictionary<string, object>
                                {
                                    {"title", item.name },
                                    {"votesup", 0 },
                                    {"votesdown", 0 },
                                    {"description", item.description },
                                    {"score", 0 },
                                    {"views", 0 },
                                    {"created", new DateTime() },
                                };
                        }
                    }
                    SaveUrls();
                    SaveSkinInfo();

                    if (configData.WorkshopImages)
                        ServerMgr.Instance.StartCoroutine(GetWorkshopSkins());
                    else
                    {
                        if (!orderPending)
                            ServerMgr.Instance.StartCoroutine(ProcessLoadOrders());
                    }
                }
            }, this);
        }

        private IEnumerator GetWorkshopSkins()
        {
            var query = Rust.Global.SteamServer.Workshop.CreateQuery();
            query.Page = 1;
            query.PerPage = 50000;
            query.RequireTags.Add("version3");
            query.RequireTags.Add("skin");
            query.RequireAllTags = true;
            query.Run();
            Puts("Querying Steam for available workshop items. Please wait for a response from Steam...");
            yield return new WaitWhile(() => query.IsRunning);
            Puts($"Found {query.Items.Length} workshop items. Gathering image URLs");

            foreach (var item in query.Items)
            {
                if (!string.IsNullOrEmpty(item.PreviewImageUrl))
                {
                    foreach (var tag in item.Tags)
                    {
                        var adjTag = tag.ToLower().Replace("skin", "").Replace(" ", "").Replace("-", "");
                        if (workshopNameToShortname.ContainsKey(adjTag))
                        {
                            string identifier = $"{workshopNameToShortname[adjTag]}_{item.Id}";

                            if (!imageUrls.URLs.ContainsKey(identifier))
                                imageUrls.URLs.Add(identifier, item.PreviewImageUrl);

                            skinInformation.skinData[identifier] = new Dictionary<string, object>
                                {
                                    {"title", item.Title },
                                    {"votesup", item.VotesUp },
                                    {"votesdown", item.VotesDown },
                                    {"description", item.Description },
                                    {"score", item.Score },
                                    {"views", item.WebsiteViews },
                                    {"created", item.Created },
                                };
                        }
                    }
                }
            }
            query.Dispose();
            SaveUrls();
            SaveSkinInfo();
        }

        private IEnumerator ProcessLoadOrders()
        {
            yield return new WaitWhile(() => !isInitialized);

            if (loadOrders.Count > 0)
            {
                if (orderPending)
                    yield break;

                LoadOrder nextLoad = loadOrders.Dequeue();
                if (!nextLoad.loadSilent)
                    Puts("Starting order " + nextLoad.loadName);

                if (nextLoad.imageList.Count > 0)
                {
                    foreach (var item in nextLoad.imageList)
                        assets.Add(item.Key, item.Value);
                }
                if (nextLoad.imageData.Count > 0)
                {
                    foreach (var item in nextLoad.imageData)
                        assets.Add(item.Key, null, item.Value);
                }

                orderPending = true;

                assets.BeginLoad(nextLoad.loadSilent ? string.Empty : nextLoad.loadName);
            }
        }

        private void GetPlayerAvatar(string userId)
        {
            if (!configData.StoreAvatars || string.IsNullOrEmpty(userId) || HasImage(userId, 0))
                return;

            webrequest.Enqueue($"http://steamcommunity.com/profiles/{userId}?xml=1", null, (code, response) =>
            {
                if (response != null && code == 200)
                {
                    string avatar = avatarFilter.Match(response).Groups[1].ToString();
                    if (!string.IsNullOrEmpty(avatar))
                        AddImage(avatar, userId, 0);
                }
            }, this);
        }

        private void RefreshImagery()
        {
            imageIdentifiers.imageIds.Clear();
            imageIdentifiers.lastCEID = CommunityEntity.ServerInstance.net.ID;

            AddImage("http://i.imgur.com/sZepiWv.png", "NONE", 0);
            AddImage("http://i.imgur.com/lydxb0u.png", "LOADING", 0);
            foreach (var image in configData.UserImages)
            {
                if (!string.IsNullOrEmpty(image.Value))
                    AddImage(image.Value, image.Key, 0);
            }

            GetItemSkins();
        }

        private void CheckForRefresh()
        {
            if (assets == null)
                assets = new GameObject("WebObject").AddComponent<ImageAssets>();

            isInitialized = true;

            if (imageIdentifiers.lastCEID != CommunityEntity.ServerInstance.net.ID)
            {
                if (imageIdentifiers.imageIds.Count < 2)
                {
                    RefreshImagery();
                }
                else
                {
                    PrintWarning("The CommunityEntity instance ID has changed! Due to the way CUI works in Rust all previously stored images must be removed and re-stored using the new ID as reference so clients can find the images. These images will be added to a new load order. Interupting this process will result in being required to re-download these images from the web");
                    RestoreLoadedImages();
                }
            }
        }
        private void RestoreLoadedImages()
        {
            orderPending = true;
            int failed = 0;

            Dictionary<string, byte[]> oldFiles = new Dictionary<string, byte[]>();

            for (int i = imageIdentifiers.imageIds.Count - 1; i >= 0; i--)
            {
                var image = imageIdentifiers.imageIds.ElementAt(i);

                uint imageId;
                if (!uint.TryParse(image.Value, out imageId))
                    continue;

                byte[] bytes = FileStorage.server.Get(imageId, FileStorage.Type.png, imageIdentifiers.lastCEID);
                if (bytes != null)
                    oldFiles.Add(image.Key, bytes);
                else
                {
                    failed++;
                    imageIdentifiers.imageIds.Remove(image.Key);
                }
            }

            Facepunch.Sqlite.Database db = new Facepunch.Sqlite.Database();
            try
            {
                db.Open($"{ConVar.Server.rootFolder}/sv.files.0.db");
                db.Execute("DELETE FROM data WHERE entid = ?", imageIdentifiers.lastCEID);
                db.Close();
            }
            catch { }

            loadOrders.Enqueue(new LoadOrder("Image restoration from previous database", oldFiles));
            PrintWarning($"{imageIdentifiers.imageIds.Count - failed} images queued for restoration, {failed} images failed");
            imageIdentifiers.lastCEID = CommunityEntity.ServerInstance.net.ID;
            SaveData();

            orderPending = false;
            ServerMgr.Instance.StartCoroutine(ProcessLoadOrders());
        }

        #endregion Functions

        #region Workshop Names and Image URLs

        private readonly Dictionary<string, string> workshopNameToShortname = new Dictionary<string, string>
        {
            {"ak47", "rifle.ak" },
            {"balaclava", "mask.balaclava" },
            {"bandana", "mask.bandana" },
            {"bearrug", "rug.bear" },
            {"beenie", "hat.beenie" },
            {"boltrifle", "rifle.bolt" },
            {"boonie", "hat.boonie" },
            {"buckethat", "bucket.helmet" },
            {"burlapgloves", "burlap.gloves" },
            {"burlappants", "burlap.trousers" },
            {"cap", "hat.cap" },
            {"collaredshirt", "shirt.collared" },
            {"deerskullmask", "deer.skull.mask" },
            {"hideshirt", "attire.hide.vest" },
            {"hideshoes", "attire.hide.boots" },
            {"longtshirt", "tshirt.long" },
            {"lr300", "rifle.lr300" },
            {"minerhat", "hat.miner" },
            {"mp5", "smg.mp5" },
            {"pipeshotgun", "shotgun.waterpipe" },
            {"roadsignpants", "roadsign.kilt" },
            {"roadsignvest", "roadsign.jacket" },
            {"semiautopistol", "pistol.semiauto" },
            {"snowjacket", "jacket.snow" },
            {"sword", "salvaged.sword" },
            {"vagabondjacket", "jacket" },
            {"woodstorage", "box.wooden" },
            {"workboots", "shoes.boots" }
        };

        private readonly Hash<string, string> defaultUrls = new Hash<string, string>
        {
            {"ammo.handmade.shell","http://i.imgur.com/V0CyZ7j.png"},
            {"ammo.pistol","http://i.imgur.com/gDNR7oj.png"},
            {"ammo.pistol.fire","http://i.imgur.com/VyX0pAu.png"},
            {"ammo.pistol.hv","http://i.imgur.com/E1dB4Nb.png"},
            {"ammo.rifle","http://i.imgur.com/rqVkjX3.png"},
            {"ammo.rifle.explosive","http://i.imgur.com/hpAxKQc.png"},
            {"ammo.rifle.hv","http://i.imgur.com/BkG4hLM.png"},
            {"ammo.rifle.incendiary","http://i.imgur.com/SN4XV2S.png"},
            {"ammo.rocket.basic","http://i.imgur.com/Weg1M6y.png"},
            {"ammo.rocket.fire","http://i.imgur.com/j4XMSmO.png"},
            {"ammo.rocket.hv","http://i.imgur.com/5mdVIIV.png"},
            {"ammo.rocket.smoke","http://i.imgur.com/kMTgSEI.png"},
            {"ammo.shotgun","http://i.imgur.com/caFY5Bp.png"},
            {"ammo.shotgun.fire", "http://i.imgur.com/FbIMeaK.png"},
            {"ammo.shotgun.slug","http://i.imgur.com/ti5fCBp.png"},
            {"antiradpills","http://i.imgur.com/SIhXEtB.png"},
            {"apple","http://i.imgur.com/goMCM2w.png"},
            {"apple.spoiled","http://i.imgur.com/2pi2sUH.png"},
            {"arrow.bone", "http://i.imgur.com/wpIJhaO.png"},
            {"arrow.fire", "http://i.imgur.com/AT0WVsQ.png"},
            {"arrow.hv","http://i.imgur.com/r6VLTt2.png"},
            {"arrow.wooden","http://i.imgur.com/yMCfjKh.png"},
            {"attire.hide.boots","http://i.imgur.com/6S98FbC.png"},
            {"attire.hide.helterneck","http://i.imgur.com/2RXe7cg.png"},
            {"attire.hide.pants","http://i.imgur.com/rJy27KQ.png"},
            {"attire.hide.poncho","http://i.imgur.com/cqHND3g.png"},
            {"attire.hide.skirt","http://i.imgur.com/nRlYLJW.png"},
            {"attire.hide.vest","http://i.imgur.com/RQ8LJ5q.png"},
            {"autoturret","http://i.imgur.com/4R0ByHj.png"},
            {"axe.salvaged","http://i.imgur.com/muTaCg2.png"},
            {"bandage","http://i.imgur.com/TuMpnnu.png"},
            {"barricade.concrete","http://i.imgur.com/91Ob9XP.png"},
            {"barricade.metal","http://i.imgur.com/7rseBMC.png"},
            {"barricade.sandbags","http://i.imgur.com/gBQLSgQ.png"},
            {"barricade.stone","http://i.imgur.com/W8qTCEX.png"},
            {"barricade.wood","http://i.imgur.com/ycYTO3W.png"},
            {"barricade.woodwire","http://i.imgur.com/PMEFBla.png"},
            {"battery.small","http://i.imgur.com/214z05n.png"},
            {"bearmeat","http://i.imgur.com/hpL2I64.png"},
            {"bearmeat.burned","http://i.imgur.com/f1eVA0W.png"},
            {"bearmeat.cooked","http://i.imgur.com/e5Z6w1y.png"},
            {"bed","http://i.imgur.com/K0zQtwh.png"},
            {"black.raspberries","http://i.imgur.com/HZjKpX9.png"},
            {"bleach","http://i.imgur.com/jhjh0gU.png"},
            {"blood","http://i.imgur.com/Mdtvg2m.png"},
            {"blueberries","http://i.imgur.com/tFZ66fB.png"},
            {"blueprintbase","http://i.imgur.com/hJUDFv3.png"},
            {"blueprulongbase","http://i.imgur.com/gMdRr6G.png"},
            {"bone.armor.suit","http://i.imgur.com/FkFR1kX.png"},
            {"bone.club","http://i.imgur.com/ib11D8V.png"},
            {"bone.fragments","http://i.imgur.com/iOJbBGT.png"},
            {"botabag","http://i.imgur.com/MkIOiUs.png"},
            {"bow.hunting","http://i.imgur.com/Myv79jT.png"},
            {"box.repair.bench","http://i.imgur.com/HpwYNjI.png"},
            {"box.wooden","http://i.imgur.com/dFqTUTQ.png"},
            {"box.wooden.large","http://i.imgur.com/qImBEtL.png"},
            {"bucket.helmet","http://i.imgur.com/Sb5cnpz.png"},
            {"bucket.water","http://i.imgur.com/svlCdlv.png"},
            {"building.planner","http://i.imgur.com/oXu5F27.png"},
            {"burlap.gloves","http://i.imgur.com/8aFVMgl.png"},
            {"burlap.headwrap","http://i.imgur.com/u6YLWda.png"},
            {"burlap.shirt","http://i.imgur.com/MUs4xL6.png"},
            {"burlap.shoes","http://i.imgur.com/wXrkSxd.png"},
            {"burlap.trousers","http://i.imgur.com/tDqEh7T.png"},
            {"cactusflesh","http://i.imgur.com/8R16YDP.png"},
            {"campfire","http://i.imgur.com/TiAlJpv.png"},
            {"can.beans","http://i.imgur.com/Ysn6ThW.png"},
            {"can.beans.empty","http://i.imgur.com/9K5In35.png"},
            {"can.tuna","http://i.imgur.com/c8rDUP3.png"},
            {"can.tuna.empty","http://i.imgur.com/GB02zHx.png"},
            {"candycane","http://i.imgur.com/DSxrXOI.png"},
            {"cctv.camera","http://i.imgur.com/4j4LD01.png"},
            {"ceilinglight","http://i.imgur.com/3sikyL6.png"},
            {"chainsaw", "http://i.imgur.com/B0fm4Hp.png"},
            {"chair","http://i.imgur.com/AvNnKqU.png"},
            {"charcoal","http://i.imgur.com/G2hyxqi.png"},
            {"chicken.burned","http://i.imgur.com/34sYfir.png"},
            {"chicken.cooked","http://i.imgur.com/UvHbBhH.png"},
            {"chicken.raw","http://i.imgur.com/gMldKSz.png"},
            {"chicken.spoiled","http://i.imgur.com/hiOEwGn.png"},
            {"chocholate","http://i.imgur.com/Ymq7PsV.png"},
            {"clone.corn","http://i.imgur.com/YLMueNU.png"},
            {"clone.hemp","http://i.imgur.com/VbVPS5l.png"},
            {"clone.pumpkin","http://i.imgur.com/MbBU6bB.png"},
            {"cloth","http://i.imgur.com/0olknLW.png"},
            {"coal","http://i.imgur.com/SIWOdbj.png"},
            {"coffeecan.helmet","http://i.imgur.com/RrY8aMM.png"},
            {"corn","http://i.imgur.com/6V5SJZ0.png"},
            {"crossbow","http://i.imgur.com/nDBFhTA.png"},
            {"crude.oil","http://i.imgur.com/VmQvwPS.png"},
            {"cupboard.tool","http://i.imgur.com/OzUewI1.png"},
            {"deer.skull.mask","http://i.imgur.com/sqLjUSE.png"},
            {"deermeat.burned","http://i.imgur.com/f1eVA0W.png"},
            {"deermeat.cooked","http://i.imgur.com/e5Z6w1y.png"},
            {"deermeat.raw","http://i.imgur.com/hpL2I64.png"},
            {"door.closer","http://i.imgur.com/QIKkGqT.png"},
            {"door.double.hinged.metal","http://i.imgur.com/awNuhRv.png"},
            {"door.double.hinged.toptier","http://i.imgur.com/oJCqHd6.png"},
            {"door.double.hinged.wood","http://i.imgur.com/tcHmZXZ.png"},
            {"door.hinged.metal","http://i.imgur.com/UGZftiQ.png"},
            {"door.hinged.toptier","http://i.imgur.com/bc2TrfQ.png"},
            {"door.hinged.wood","http://i.imgur.com/PrrWSN2.png"},
            {"door.key","http://i.imgur.com/kw8UAN2.png"},
            {"dropbox", "http://i.imgur.com/KqV8FcU.png"},
            {"ducttape","http://i.imgur.com/llXWS6p.png"},
            {"explosive.satchel","http://i.imgur.com/dlUW54q.png"},
            {"explosive.timed","http://i.imgur.com/CtxUCgC.png"},
            {"explosives","http://i.imgur.com/S43G64k.png"},
            {"fat.animal","http://i.imgur.com/7NdUBKm.png"},
            {"fish.cooked","http://i.imgur.com/Idtzv1t.png"},
            {"fish.minnows","http://i.imgur.com/7LXZH2S.png"},
            {"fish.raw","http://i.imgur.com/GdErxqf.png"},
            {"fish.troutsmall","http://i.imgur.com/aJ2PquF.png"},
            {"fishtrap.small","http://i.imgur.com/spuGlOj.png"},
            {"flamethrower","http://i.imgur.com/CwhZ8i7.png"},
            {"flameturret","http://i.imgur.com/PA38S2I.png"},
            {"flare","http://i.imgur.com/MS0JcRT.png"},
            {"floor.grill","http://i.imgur.com/bp7ZOkE.png"},
            {"floor.ladder.hatch","http://i.imgur.com/suML6jj.png"},
            {"fridge","http://i.imgur.com/BmJnSYi.png"},
            {"fun.guitar","http://i.imgur.com/l96owHe.png"},
            {"furnace","http://i.imgur.com/77i4nqb.png"},
            {"furnace.large","http://i.imgur.com/NmsmUzo.png"},
            {"gates.external.high.stone","http://i.imgur.com/o4NWWXp.png"},
            {"gates.external.high.wood","http://i.imgur.com/DRa9a8G.png"},
            {"gears","http://i.imgur.com/xLtFgiI.png"},
            {"geiger.counter", "http://i.imgur.com/29GTEv2.png"},
            {"generator.wind.scrap","http://i.imgur.com/fuQaE1H.png"},
            {"glue","http://i.imgur.com/uy952o4.png"},
            {"granolabar","http://i.imgur.com/3rvzSwj.png"},
            {"grenade.beancan","http://i.imgur.com/FQZOd7m.png"},
            {"grenade.f1","http://i.imgur.com/ZwrVuXh.png"},
            {"gunpowder","http://i.imgur.com/qV7b4WD.png"},
            {"guntrap", "http://i.imgur.com/iNFOxbT.png"},
            {"hammer","http://i.imgur.com/KNG2Gvs.png"},
            {"hammer.salvaged","http://i.imgur.com/5oh3Wke.png"},
            {"hat.beenie","http://i.imgur.com/yDkGk47.png"},
            {"hat.boonie","http://i.imgur.com/2b4OjxB.png"},
            {"hat.candle","http://i.imgur.com/F7nP0PC.png"},
            {"hat.cap","http://i.imgur.com/TfycJC9.png"},
            {"hat.miner","http://i.imgur.com/RtRy2ne.png"},
            {"hat.wolf","http://i.imgur.com/D2Z8QjL.png"},
            {"hatchet","http://i.imgur.com/5juFLRZ.png"},
            {"hazmat.boots","http://i.imgur.com/sfU4PdX.png"},
            {"hazmat.gloves","http://i.imgur.com/JYTXvnx.png"},
            {"hazmat.helmet","http://i.imgur.com/BHSrFsh.png"},
            {"hazmat.jacket","http://i.imgur.com/uKk9ghN.png"},
            {"hazmat.pants","http://i.imgur.com/ZsaLNUK.png"},
            {"hazmatsuit","http://i.imgur.com/HJeL3SB.png"},
            {"heavy.plate.helmet","http://i.imgur.com/jITLARt.png"},
            {"heavy.plate.jacket","http://i.imgur.com/6NK8MLq.png"},
            {"heavy.plate.pants","http://i.imgur.com/o0HcHwc.png"},
            {"hoodie","http://i.imgur.com/EvGigZB.png"},
            {"hq.metal.ore","http://i.imgur.com/kdBrQ2P.png"},
            {"humanmeat.burned","http://i.imgur.com/DloSZvl.png"},
            {"humanmeat.cooked","http://i.imgur.com/ba2j2rG.png"},
            {"humanmeat.raw","http://i.imgur.com/28SpF8Y.png"},
            {"humanmeat.spoiled","http://i.imgur.com/mSWVRUi.png"},
            {"icepick.salvaged","http://i.imgur.com/ZTJLWdI.png"},
            {"jacket","http://i.imgur.com/zU7TQPR.png"},
            {"jacket.snow","http://i.imgur.com/32ZO3jO.png"},
            {"jackolantern.angry","http://i.imgur.com/NRdMCfb.png"},
            {"jackolantern.happy","http://i.imgur.com/2gIfuAO.png"},
            {"knife.bone","http://i.imgur.com/9TaVbYX.png"},
            {"ladder.wooden.wall","http://i.imgur.com/E3haHSe.png"},
            {"lantern","http://i.imgur.com/UHQdu3Q.png"},
            {"largemedkit","http://i.imgur.com/iPsWViD.png"},
            {"leather","http://i.imgur.com/9rqWrIy.png"},
            {"lmg.m249","http://i.imgur.com/f7Rzrn2.png"},
            {"lock.code","http://i.imgur.com/pAXI8ZY.png"},
            {"lock.key","http://i.imgur.com/HuelWn0.png"},
            {"locker","http://i.imgur.com/vBjaQ1L.png"},
            {"longsword","http://i.imgur.com/1StsKVe.png"},
            {"lowgradefuel","http://i.imgur.com/CSNPLYX.png"},
            {"mace","http://i.imgur.com/OtsvCkC.png"},
            {"machete","http://i.imgur.com/KfwkwV8.png"},
            {"mailbox", "http://i.imgur.com/DaDrDIK.png"},
            {"map","http://i.imgur.com/u8HBelr.png"},
            {"mask.balaclava","http://i.imgur.com/BYFgE5c.png"},
            {"mask.bandana","http://i.imgur.com/PImuCst.png"},
            {"meat.boar","http://i.imgur.com/4ijrHrn.png"},
            {"meat.pork.burned","http://i.imgur.com/5Dam9qQ.png"},
            {"meat.pork.cooked","http://i.imgur.com/yhgxCPG.png"},
            {"metal.facemask","http://i.imgur.com/BPd5q6h.png"},
            {"metal.fragments","http://i.imgur.com/1bzDvUs.png"},
            {"metal.ore","http://i.imgur.com/yrTGHvv.png"},
            {"metal.plate.torso","http://i.imgur.com/lMw6ez2.png"},
            {"metal.refined","http://i.imgur.com/j2947YU.png"},
            {"metalblade","http://i.imgur.com/OlsKPFm.png"},
            {"metalpipe","http://i.imgur.com/7MBFL5S.png"},
            {"metalspring","http://i.imgur.com/8GDTUnI.png"},
            {"mining.pumpjack","http://i.imgur.com/FWbMASw.png"},
            {"mining.quarry","http://i.imgur.com/4Mgh1nK.png"},
            {"mushroom","http://i.imgur.com/FeWuvuh.png"},
            {"note","http://i.imgur.com/AM3Uech.png"},
            {"pants","http://i.imgur.com/iiFJAso.png"},
            {"pants.shorts","http://i.imgur.com/BQgTzlT.png"},
            {"paper","http://i.imgur.com/pK49c6M.png"},
            {"pickaxe","http://i.imgur.com/QNirWhG.png"},
            {"pistol.eoka","http://i.imgur.com/SSb9czm.png"},
            {"pistol.m92","http://i.imgur.com/dEwdnmG.png"},
            {"pistol.python","http://i.imgur.com/67kllOx.png"},
            {"pistol.revolver","http://i.imgur.com/C6BHyBB.png"},
            {"pistol.semiauto","http://i.imgur.com/Zwqg3ic.png"},
            {"planter.large","http://i.imgur.com/c5HGHsx.png"},
            {"planter.small","http://i.imgur.com/dE1Th2A.png"},
            {"pookie.bear","http://i.imgur.com/KJSccj0.png"},
            {"propanetank","http://i.imgur.com/T5Fqxcv.png"},
            {"pumpkin","http://i.imgur.com/Gb9NvdQ.png"},
            {"research.table","http://i.imgur.com/C9wL7Kk.png"},
            {"researchpaper","http://i.imgur.com/Pv8jxrl.png"},
            {"rifle.ak","http://i.imgur.com/qlgloXW.png"},
            {"rifle.bolt","http://i.imgur.com/8oVVXJS.png"},
            {"rifle.lr300","http://i.imgur.com/NYffUwv.png"},
            {"rifle.semiauto","http://i.imgur.com/UfGP5kq.png"},
            {"riflebody","http://i.imgur.com/h90OZEg.png"},
            {"riot.helmet","http://i.imgur.com/NlxGOum.png"},
            {"roadsign.jacket","http://i.imgur.com/tqpDp2V.png"},
            {"roadsign.kilt","http://i.imgur.com/WLh1Nv4.png"},
            {"roadsigns","http://i.imgur.com/iImEIvW.png"},
            {"rock","http://i.imgur.com/2GMBs5M.png"},
            {"rocket.launcher","http://i.imgur.com/2yDyb9p.png"},
            {"rope","http://i.imgur.com/ywHRqW8.png"},
            {"rug","http://i.imgur.com/LvJNT1B.png"},
            {"rug.bear", "http://i.imgur.com/Fn79eMP.png"},
            {"salvaged.cleaver","http://i.imgur.com/DrelWEg.png"},
            {"salvaged.sword","http://i.imgur.com/M6gWbNv.png"},
            {"santahat","http://i.imgur.com/bmOV0aX.png"},
            {"scrap", "http://i.imgur.com/DmszIgB.png"},
            {"searchlight", "http://i.imgur.com/L9Nxhdv.png"},
            {"seed.corn","http://i.imgur.com/u9ZPaeG.png"},
            {"seed.hemp","http://i.imgur.com/wO6aojb.png"},
            {"seed.pumpkin","http://i.imgur.com/mHaV8ei.png"},
            {"semibody","http://i.imgur.com/UPljd8Y.png"},
            {"sewingkit","http://i.imgur.com/KmXDM8D.png"},
            {"sheetmetal","http://i.imgur.com/1GEwiaL.png"},
            {"shelves","http://i.imgur.com/vjtdyk5.png"},
            {"shirt.collared","http://i.imgur.com/2CaYDye.png"},
            {"shirt.tanktop","http://i.imgur.com/8woukzm.png"},
            {"shoes.boots","http://i.imgur.com/b8HJ3TJ.png"},
            {"shotgun.double","http://i.imgur.com/Pm2Q4Dj.png"},
            {"shotgun.pump","http://i.imgur.com/OHRph6g.png"},
            {"shotgun.spas12", "http://i.imgur.com/WzgP1Ng.png"},
            {"shotgun.waterpipe","http://i.imgur.com/3BliJtR.png"},
            {"shutter.metal.embrasure.a","http://i.imgur.com/1ke0LVO.png"},
            {"shutter.metal.embrasure.b","http://i.imgur.com/uRtgNRH.png"},
            {"shutter.wood.a","http://i.imgur.com/VngPUi2.png"},
            {"sign.hanging","http://i.imgur.com/VIeRGh9.png"},
            {"sign.hanging.banner.large","http://i.imgur.com/Owr3668.png"},
            {"sign.hanging.ornate","http://i.imgur.com/nQ1xHYb.png"},
            {"sign.pictureframe.landscape","http://i.imgur.com/nNh1uro.png"},
            {"sign.pictureframe.portrait","http://i.imgur.com/CQr8UYq.png"},
            {"sign.pictureframe.tall","http://i.imgur.com/3b51GfA.png"},
            {"sign.pictureframe.xl","http://i.imgur.com/3zdBDqa.png"},
            {"sign.pictureframe.xxl","http://i.imgur.com/9xSgewe.png"},
            {"sign.pole.banner.large","http://i.imgur.com/nGRDZrO.png"},
            {"sign.post.double","http://i.imgur.com/CXUsPSn.png"},
            {"sign.post.single","http://i.imgur.com/0qXuSMs.png"},
            {"sign.post.town","http://i.imgur.com/KgN4T1C.png"},
            {"sign.post.town.roof","http://i.imgur.com/hCLJXg4.png"},
            {"sign.wooden.huge","http://i.imgur.com/DehcZTb.png"},
            {"sign.wooden.large","http://i.imgur.com/BItcvBB.png"},
            {"sign.wooden.medium","http://i.imgur.com/zXJcB26.png"},
            {"sign.wooden.small","http://i.imgur.com/wfDYYYW.png"},
            {"skull.human","http://i.imgur.com/ZFnWubS.png"},
            {"skull.wolf","http://i.imgur.com/f4MRE72.png"},
            {"sleepingbag","http://i.imgur.com/oJes3Lo.png"},
            {"small.oil.refinery","http://i.imgur.com/Qqz6RgS.png"},
            {"smallwaterbottle","http://i.imgur.com/YTLCucH.png"},
            {"smg.2","http://i.imgur.com/ElXI2uv.png"},
            {"smg.mp5","http://i.imgur.com/ohazNYk.png"},
            {"smg.thompson","http://i.imgur.com/rSQ5nHj.png"},
            {"smgbody","http://i.imgur.com/EzXRKxC.png"},
            {"spear.stone","http://i.imgur.com/Y3HstyV.png"},
            {"spear.wooden","http://i.imgur.com/7QpIs8B.png"},
            {"spikes.floor","http://i.imgur.com/Nj0yJs0.png"},
            {"spinner.wheel","http://i.imgur.com/dWTLLAy.png"},
            {"stash.small","http://i.imgur.com/fH4RWZe.png"},
            {"sticks","http://i.imgur.com/1g7YbxM.png"},
            {"stocking.large","http://i.imgur.com/di39MBT.png"},
            {"stocking.small","http://i.imgur.com/6eAg1zi.png"},
            {"stone.pickaxe","http://i.imgur.com/54azzFs.png"},
            {"stonehatchet","http://i.imgur.com/toLaFZd.png"},
            {"stones","http://i.imgur.com/cluFzuZ.png"},
            {"sulfur","http://i.imgur.com/1RTTB7k.png"},
            {"sulfur.ore","http://i.imgur.com/AdxkKGb.png"},
            {"supply.signal","http://i.imgur.com/wj6yzow.png"},
            {"surveycharge","http://i.imgur.com/UPNvuY0.png"},
            {"syringe.medical","http://i.imgur.com/DPDicE6.png"},
            {"table","http://i.imgur.com/Okz7ePi.png"},
            {"target.reactive","http://i.imgur.com/BNcKZnU.png"},
            {"targeting.computer","http://i.imgur.com/oPMPl3B.png"},
            {"tarp","http://i.imgur.com/lXtsQMy.png"},
            {"techparts","http://i.imgur.com/ajtAyzI.png"},
            {"tool.binoculars", "http://i.imgur.com/nauvcB5.png"},
            {"tool.camera","http://i.imgur.com/4AaLCfW.png"},
            {"torch","http://i.imgur.com/qKYxg5E.png"},
            {"trap.bear","http://i.imgur.com/GZD4bVy.png"},
            {"trap.landmine","http://i.imgur.com/YR0lVCs.png"},
            {"tshirt","http://i.imgur.com/SAD8dWX.png"},
            {"tshirt.long","http://i.imgur.com/KPxtIQI.png"},
            {"tunalight","http://i.imgur.com/O1u8qqd.png"},
            {"vending.machine","http://i.imgur.com/LnWiZPZ.png"},
            {"wall.external.high","http://i.imgur.com/mB8oila.png"},
            {"wall.external.high.stone","http://i.imgur.com/7t3BdwH.png"},
            {"wall.frame.cell","http://i.imgur.com/oLj65GS.png"},
            {"wall.frame.cell.gate","http://i.imgur.com/iAcwJmG.png"},
            {"wall.frame.fence","http://i.imgur.com/4HVSY9Y.png"},
            {"wall.frame.fence.gate","http://i.imgur.com/mpmO78C.png"},
            {"wall.frame.netting","http://i.imgur.com/HWm5Zuy.png"},
            {"wall.frame.shopfront","http://i.imgur.com/G7fB7kk.png"},
            {"wall.frame.shopfront.metal","http://i.imgur.com/kcFplwc.png"},
            {"wall.window.bars.metal","http://i.imgur.com/QmkIpkZ.png"},
            {"wall.window.bars.toptier","http://i.imgur.com/AsMdaCc.png"},
            {"wall.window.bars.wood","http://i.imgur.com/VS3SVVB.png"},
            {"water","http://i.imgur.com/xdz5L7M.png"},
            {"water.barrel","http://i.imgur.com/JsmzCeU.png"},
            {"water.catcher.large","http://i.imgur.com/YWrJQoa.png"},
            {"water.catcher.small","http://i.imgur.com/PTXcYXs.png"},
            {"water.purifier","http://i.imgur.com/L7R4Ral.png"},
            {"water.salt","http://i.imgur.com/d4ihUtv.png"},
            {"waterjug","http://i.imgur.com/BJzeMkc.png"},
            {"weapon.mod.flashlight","http://i.imgur.com/4gFapPt.png"},
            {"weapon.mod.holosight","http://i.imgur.com/R76B83t.png"},
            {"weapon.mod.lasersight","http://i.imgur.com/rxIzDwY.png"},
            {"weapon.mod.muzzleboost","http://i.imgur.com/U9aMaPN.png"},
            {"weapon.mod.muzzlebrake","http://i.imgur.com/sjxJIjT.png"},
            {"weapon.mod.silencer","http://i.imgur.com/oighpzk.png"},
            {"weapon.mod.simplesight", "http://i.imgur.com/D8lbB75.png"},
            {"weapon.mod.small.scope","http://i.imgur.com/jMvDHLz.png"},
            {"wolfmeat.burned","http://i.imgur.com/zAJhDNd.png"},
            {"wolfmeat.cooked","http://i.imgur.com/LKlgpMe.png"},
            {"wolfmeat.raw","http://i.imgur.com/qvMvis8.png"},
            {"wolfmeat.spoiled","http://i.imgur.com/8kXOVyJ.png"},
            {"wood","http://i.imgur.com/AChzDls.png"},
            {"wood.armor.helmet", "http://i.imgur.com/ByKb3BS.png"},
            {"wood.armor.jacket","http://i.imgur.com/9PUyVIv.png"},
            {"wood.armor.pants","http://i.imgur.com/k2O9xEX.png"},
            {"xmas.present.large","http://i.imgur.com/dU3nhYo.png"},
            {"xmas.present.medium","http://i.imgur.com/Ov5YUty.png"},
            {"xmas.present.small","http://i.imgur.com/hWCd67B.png"}
       };

        #endregion Workshop Names and Image URLs

        #region API

        [HookMethod("AddImage")]
        public bool AddImage(string url, string imageName, ulong imageId)
        {
            loadOrders.Enqueue(new LoadOrder(imageName, new Dictionary<string, string> { { $"{imageName}_{imageId}", url } }, true));
            if (!orderPending)
                ServerMgr.Instance.StartCoroutine(ProcessLoadOrders());
            return true;
        }

        [HookMethod("AddImageData")]
        public bool AddImageData(string imageName, byte[] array, ulong imageId)
        {
            loadOrders.Enqueue(new LoadOrder(imageName, new Dictionary<string, byte[]> { { $"{imageName}_{imageId}", array } }, true));
            if (!orderPending)
                ServerMgr.Instance.StartCoroutine(ProcessLoadOrders());
            return true;
        }

        [HookMethod("GetImageURL")]
        public string GetImageURL(string imageName, ulong imageId = 0)
        {
            string identifier = $"{imageName}_{imageId}";
            string value;
            if (imageUrls.URLs.TryGetValue(identifier, out value))
                return value;
            return imageIdentifiers.imageIds["NONE_0"];
        }

        [HookMethod("GetImage")]
        public string GetImage(string imageName, ulong imageId = 0, bool returnUrl = false)
        {
            string identifier = $"{imageName}_{imageId}";
            string value;
            if (imageIdentifiers.imageIds.TryGetValue(identifier, out value))
                return value;
            else
            {
                if (imageUrls.URLs.TryGetValue(identifier, out value))
                {
                    AddImage(value, imageName, imageId);
                    return imageIdentifiers.imageIds["LOADING_0"];
                }
            }

            if (returnUrl && !string.IsNullOrEmpty(value))
                return value;

            return imageIdentifiers.imageIds["NONE_0"];
        }

        [HookMethod("GetImageList")]
        public List<ulong> GetImageList(string name)
        {
            List<ulong> skinIds = new List<ulong>();
            var matches = imageUrls.URLs.Keys.Where(x => x.StartsWith(name)).ToArray();
            for (int i = 0; i < matches.Length; i++)
            {
                var index = matches[i].IndexOf("_");
                if (matches[i].Substring(0, index) == name)
                {
                    ulong skinID;
                    if (ulong.TryParse(matches[i].Substring(index + 1), out skinID))
                        skinIds.Add(ulong.Parse(matches[i].Substring(index + 1)));
                }
            }
            return skinIds;
        }

        [HookMethod("GetSkinInfo")]
        public Dictionary<string, object> GetSkinInfo(string name, ulong id)
        {
            Dictionary<string, object> skinInfo;
            if (skinInformation.skinData.TryGetValue($"{name}_{id}", out skinInfo))
                return skinInfo;
            return null;
        }

        [HookMethod("HasImage")]
        public bool HasImage(string imageName, ulong imageId)
        {
            if (imageIdentifiers.imageIds.ContainsKey($"{imageName}_{imageId}") && IsInStorage(uint.Parse(imageIdentifiers.imageIds[$"{imageName}_{imageId}"])))
                return true;

            return false;
        }

        public bool IsInStorage(uint crc) => FileStorage.server.Get(crc, FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID) != null;

        [HookMethod("IsReady")]
        public bool IsReady() => loadOrders.Count == 0 && !orderPending;

        [HookMethod("ImportImageList")]
        public void ImportImageList(string title, Dictionary<string, string> imageList, ulong imageId = 0, bool replace = false)
        {
            Dictionary<string, string> newLoadOrder = new Dictionary<string, string>();
            foreach (var image in imageList)
            {
                if (!replace && HasImage(image.Key, imageId))
                    continue;
                newLoadOrder[$"{image.Key}_{imageId}"] = image.Value;
            }
            if (newLoadOrder.Count > 0)
            {
                loadOrders.Enqueue(new LoadOrder(title, newLoadOrder));
                if (!orderPending)
                    ServerMgr.Instance.StartCoroutine(ProcessLoadOrders());
            }
        }

        [HookMethod("ImportItemList")]
        public void ImportItemList(string title, Dictionary<string, Dictionary<ulong, string>> itemList, bool replace = false)
        {
            Dictionary<string, string> newLoadOrder = new Dictionary<string, string>();
            foreach (var image in itemList)
            {
                foreach (var skin in image.Value)
                {
                    if (!replace && HasImage(image.Key, skin.Key))
                        continue;
                    newLoadOrder[$"{image.Key}_{skin.Key}"] = skin.Value;
                }
            }
            if (newLoadOrder.Count > 0)
            {
                loadOrders.Enqueue(new LoadOrder(title, newLoadOrder));
                if (!orderPending)
                    ServerMgr.Instance.StartCoroutine(ProcessLoadOrders());
            }
        }

        [HookMethod("ImportImageData")]
        public void ImportImageData(string title, Dictionary<string, byte[]> imageList, ulong imageId = 0, bool replace = false)
        {
            Dictionary<string, byte[]> newLoadOrder = new Dictionary<string, byte[]>();
            foreach (var image in imageList)
            {
                if (!replace && HasImage(image.Key, imageId))
                    continue;
                newLoadOrder[$"{image.Key}_{imageId}"] = image.Value;
            }
            if (newLoadOrder.Count > 0)
            {
                loadOrders.Enqueue(new LoadOrder(title, newLoadOrder));
                if (!orderPending)
                    ServerMgr.Instance.StartCoroutine(ProcessLoadOrders());
            }
        }

        [HookMethod("LoadImageList")]
        public void LoadImageList(string title, List<KeyValuePair<string, ulong>> imageList)
        {
            Dictionary<string, string> newLoadOrder = new Dictionary<string, string>();
            foreach (var image in imageList)
            {
                if (HasImage(image.Key, image.Value))
                    continue;
                string identifier = $"{image.Key}_{image.Value}";
                if (imageUrls.URLs.ContainsKey(identifier))
                    newLoadOrder[identifier] = imageUrls.URLs[identifier];
            }
            if (newLoadOrder.Count > 0)
            {
                loadOrders.Enqueue(new LoadOrder(title, newLoadOrder));
                if (!orderPending)
                    ServerMgr.Instance.StartCoroutine(ProcessLoadOrders());
            }
        }

        [HookMethod("RemoveImage")]
        public void RemoveImage(string imageName, ulong imageId)
        {
            if (HasImage(imageName, imageId))
                return;

            uint crc = uint.Parse(GetImage(imageName, imageId));
            FileStorage.server.Remove(crc, FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID);
        }

        #endregion API

        #region Commands

        [ConsoleCommand("workshopimages")]
        private void cmdWorkshopImages(ConsoleSystem.Arg arg)
        {
            if (arg.Connection == null || arg.Connection.authLevel > 0)
            {
                ServerMgr.Instance.StartCoroutine(GetWorkshopSkins());
            }
        }

        [ConsoleCommand("cancelstorage")]
        private void cmdCancelStorage(ConsoleSystem.Arg arg)
        {
            if (arg.Connection == null || arg.Connection.authLevel > 0)
            {
                if (!orderPending)
                    PrintWarning("No images are currently being downloaded");
                else
                {
                    assets.ClearList();
                    loadOrders.Clear();
                    PrintWarning("Pending image downloads have been cancelled!");
                }
            }
        }

        private List<ulong> pendingAnswers = new List<ulong>();

        [ConsoleCommand("refreshallimages")]
        private void cmdRefreshAllImages(ConsoleSystem.Arg arg)
        {
            if (arg.Connection == null || arg.Connection.authLevel > 0)
            {
                SendReply(arg, "Running this command will wipe all of your ImageLibrary data, meaning every registered image will need to be re-downloaded. Are you sure you wish to continue? (type yes or no)");

                ulong userId = arg.Connection == null || arg.IsRcon ? 0U : arg.Connection.userid;
                if (!pendingAnswers.Contains(userId))
                {
                    pendingAnswers.Add(userId);
                    timer.In(5, () =>
                    {
                        if (pendingAnswers.Contains(userId))
                            pendingAnswers.Remove(userId);
                    });
                }
            }
        }

        [ConsoleCommand("yes")]
        private void cmdRefreshAllImagesYes(ConsoleSystem.Arg arg)
        {
            if (arg.Connection == null || arg.Connection.authLevel > 0)
            {
                PrintWarning("Wiping ImageLibrary data and redownloading ImageLibrary specific images. All plugins that have registered images via ImageLibrary will need to be re-loaded!");
                RefreshImagery();

                ulong userId = arg.Connection == null || arg.IsRcon ? 0U : arg.Connection.userid;
                if (pendingAnswers.Contains(userId))
                    pendingAnswers.Remove(userId);
            }
        }

        [ConsoleCommand("no")]
        private void cmdRefreshAllImagesNo(ConsoleSystem.Arg arg)
        {
            if (arg.Connection == null || arg.Connection.authLevel > 0)
            {
                SendReply(arg, "ImageLibrary data wipe aborted!");
                ulong userId = arg.Connection == null || arg.IsRcon ? 0U : arg.Connection.userid;
                if (pendingAnswers.Contains(userId))
                    pendingAnswers.Remove(userId);
            }
        }

        #endregion Commands

        #region Image Storage

        private class LoadOrder
        {
            public string loadName;
            public bool loadSilent;

            public Dictionary<string, string> imageList = new Dictionary<string, string>();
            public Dictionary<string, byte[]> imageData = new Dictionary<string, byte[]>();

            public LoadOrder()
            {
            }
            public LoadOrder(string loadName, Dictionary<string, string> imageList, bool loadSilent = false)
            {
                this.loadName = loadName;
                this.imageList = imageList;
                this.loadSilent = loadSilent;
            }
            public LoadOrder(string loadName, Dictionary<string, byte[]> imageData, bool loadSilent = false)
            {
                this.loadName = loadName;
                this.imageData = imageData;
                this.loadSilent = loadSilent;
            }
        }

        private class ImageAssets : MonoBehaviour
        {
            private Queue<QueueItem> queueList = new Queue<QueueItem>();
            private bool isLoading;
            private double nextUpdate;
            private int listCount;
            private string request;

            private void OnDestroy()
            {
                queueList.Clear();
            }

            public void Add(string name, string url = null, byte[] bytes = null)
            {
                queueList.Enqueue(new QueueItem(name, url, bytes));
            }

            public void BeginLoad(string request)
            {
                this.request = request;
                nextUpdate = UnityEngine.Time.time + il.configData.UpdateInterval;
                listCount = queueList.Count;
                Next();
            }

            public void ClearList()
            {
                queueList.Clear();
                il.orderPending = false;
            }

            private void Next()
            {
                if (queueList.Count == 0)
                {
                    il.orderPending = false;
                    il.SaveData();
                    if (!string.IsNullOrEmpty(request))
                        print($"Image batch ({request}) has been stored successfully");

                    request = string.Empty;
                    listCount = 0;

                    StartCoroutine(il.ProcessLoadOrders());
                    return;
                }
                if (il.configData.ShowProgress && listCount > 1)
                {
                    var time = UnityEngine.Time.time;
                    if (time > nextUpdate)
                    {
                        var amountDone = listCount - queueList.Count;
                        print($"{request} storage process at {Math.Round((amountDone / (float)listCount) * 100, 0)}% ({amountDone}/{listCount})");
                        nextUpdate = time + il.configData.UpdateInterval;
                    }
                }
                isLoading = true;

                QueueItem queueItem = queueList.Dequeue();
                if (!string.IsNullOrEmpty(queueItem.url))
                    StartCoroutine(DownloadImage(queueItem));
                else StoreByteArray(queueItem.bytes, queueItem.name);
            }

            private IEnumerator DownloadImage(QueueItem info)
            {
                UnityWebRequest www = UnityWebRequest.Get(info.url);

                yield return www.SendWebRequest();
                if (il == null) yield break;
                if (www.isNetworkError || www.isHttpError)
                {
                    print(string.Format("Image failed to download! Error: {0} - Image Name: {1} - Image URL: {2}", www.error, info.name, info.url));
                    www.Dispose();
                    isLoading = false;
                    Next();
                    yield break;
                }

                Texture2D texture = new Texture2D(2, 2);
                texture.LoadImage(www.downloadHandler.data);
                if (texture != null)
                {
                    byte[] bytes = texture.EncodeToPNG();
                    DestroyImmediate(texture);
                    StoreByteArray(bytes, info.name);
                }
                www.Dispose();
            }

            private void StoreByteArray(byte[] bytes, string name)
            {
                if (bytes != null)
                    il.imageIdentifiers.imageIds[name] = FileStorage.server.Store(bytes, FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID).ToString();
                isLoading = false;
                Next();
            }

            private class QueueItem
            {
                public byte[] bytes;
                public string url;
                public string name;
                public QueueItem(string name, string url = null, byte[] bytes = null)
                {
                    this.bytes = bytes;
                    this.url = url;
                    this.name = name;
                }
            }
        }

        #endregion Image Storage

        #region Config

        private ConfigData configData;

        class ConfigData
        {
            [JsonProperty(PropertyName = "Avatars - Store player avatars")]
            public bool StoreAvatars { get; set; }

            [JsonProperty(PropertyName = "Workshop - Download workshop image information")]
            public bool WorkshopImages { get; set; }

            [JsonProperty(PropertyName = "Progress - Show download progress in console")]
            public bool ShowProgress { get; set; }

            [JsonProperty(PropertyName = "Progress - Time between update notifications")]
            public int UpdateInterval { get; set; }

            [JsonProperty(PropertyName = "User Images - Manually define images to be loaded")]
            public Dictionary<string, string> UserImages { get; set; }
        }

        private void LoadVariables()
        {
            LoadConfigVariables();
            SaveConfig();
        }
        protected override void LoadDefaultConfig()
        {
            var config = new ConfigData
            {
                ShowProgress = true,
                StoreAvatars = true,
                WorkshopImages = true,
                UpdateInterval = 20,
                UserImages = new Dictionary<string, string>()
            };
            SaveConfig(config);
        }
        private void LoadConfigVariables() => configData = Config.ReadObject<ConfigData>();
        private void SaveConfig(ConfigData config) => Config.WriteObject(config, true);

        #endregion Config

        #region Data Management

        private void SaveData() => identifiers.WriteObject(imageIdentifiers);
        private void SaveSkinInfo() => skininfo.WriteObject(skinInformation);
        private void SaveUrls() => urls.WriteObject(imageUrls);

        private void LoadData()
        {
            try
            {
                imageIdentifiers = identifiers.ReadObject<ImageIdentifiers>();
            }
            catch
            {
                imageIdentifiers = new ImageIdentifiers();
            }
            try
            {
                skinInformation = skininfo.ReadObject<SkinInformation>();
            }
            catch
            {
                skinInformation = new SkinInformation();
            }
            try
            {
                imageUrls = urls.ReadObject<ImageURLs>();
            }
            catch
            {
                imageUrls = new ImageURLs();
            }
            if (skinInformation == null)
                skinInformation = new SkinInformation();
            if (imageIdentifiers == null)
                imageIdentifiers = new ImageIdentifiers();
            if (imageUrls == null)
                imageUrls = new ImageURLs();
            if (imageUrls.URLs.Count == 0)
            {
                foreach (var item in defaultUrls)
                    imageUrls.URLs.Add($"{item.Key}_0", item.Value);
            }
        }

        private class ImageIdentifiers
        {
            public uint lastCEID;
            public Hash<string, string> imageIds = new Hash<string, string>();
        }

        private class SkinInformation
        {
            public Hash<string, Dictionary<string, object>> skinData = new Hash<string, Dictionary<string, object>>();
        }

        private class ImageURLs
        {
            public Hash<string, string> URLs = new Hash<string, string>();
        }

        #endregion Data Management
    }
}
