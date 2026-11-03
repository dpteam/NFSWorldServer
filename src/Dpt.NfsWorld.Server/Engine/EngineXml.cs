using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Dpt.NfsWorld.Core;

namespace Dpt.NfsWorld.Engine
{
    /// <summary>
    /// Builds the XML documents the game expects. Every document can be overridden
    /// per client profile by dropping a file into data/profiles/&lt;id&gt;/templates/:
    /// that is exactly how the 2010 and 2011 builds differ (tag casing, namespaces,
    /// extra fields), so a new client version is data, not code.
    /// </summary>
    public static class EngineXml
    {
        private static readonly object CacheSync = new object();
        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string Template(ClientProfile p, string name)
        {
            if (p == null) return null;
            string key = p.Id + "/" + name;
            lock (CacheSync)
            {
                string cached;
                if (Cache.TryGetValue(key, out cached)) return cached;
            }
            string file = Path.Combine(p.Root, "templates", name + ".xml");
            string text = null;
            try { if (File.Exists(file)) text = File.ReadAllText(file, Encoding.UTF8); }
            catch { }
            lock (CacheSync) Cache[key] = text;
            return text;
        }

        public static void ClearCache()
        {
            lock (CacheSync) Cache.Clear();
        }

        private static string Fill(string template, Dictionary<string, string> vars)
        {
            if (string.IsNullOrEmpty(template)) return "";
            StringBuilder sb = new StringBuilder(template);
            foreach (KeyValuePair<string, string> kv in vars)
                sb.Replace("{{" + kv.Key + "}}", kv.Value ?? "");
            return sb.ToString();
        }

        private static string N(long v) { return v.ToString(CultureInfo.InvariantCulture); }
        private static string D(double v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }

        // ------------------------------------------------------------- persona

        public static string ProfileData(Persona p, ClientProfile profile)
        {
            Dictionary<string, string> v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            v["PersonaId"] = N(p.Id);
            v["Name"] = Util.EscapeXml(p.Name);
            v["Motto"] = Util.EscapeXml(p.Motto);
            v["Level"] = N(p.Level);
            v["Rep"] = N(p.Rep);
            v["Cash"] = N(p.Cash);
            v["IconIndex"] = N(p.IconIndex);
            v["Score"] = "0";
            v["PercentToLevel"] = "0.0";
            v["Rating"] = "0";

            string tpl = Template(profile, "ProfileData");
            if (!string.IsNullOrEmpty(tpl)) return Fill(tpl, v);

            if (profile != null && string.Equals(profile.PersonaFormat, "modern", StringComparison.OrdinalIgnoreCase))
            {
                StringBuilder sb = new StringBuilder(512);
                sb.Append("<ProfileData xmlns=\"http://schemas.datacontract.org/2004/07/EA.NFSWO.ENGINE.TransferObjects.DriverPersona\"");
                sb.Append(" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">");
                sb.Append("<bio i:nil=\"true\"/><boost>0</boost>");
                sb.Append("<cash>").Append(N(p.Cash)).Append("</cash>");
                sb.Append("<clan>0</clan><clanIcon i:nil=\"true\"/>");
                sb.Append("<iconIndex>").Append(N(p.IconIndex)).Append("</iconIndex>");
                sb.Append("<level>").Append(N(p.Level)).Append("</level>");
                sb.Append("<name>").Append(Util.EscapeXml(p.Name)).Append("</name>");
                sb.Append("<nickname>").Append(Util.EscapeXml(p.Name)).Append("</nickname>");
                sb.Append("<percentToLevel>0</percentToLevel>");
                sb.Append("<personaId>").Append(N(p.Id)).Append("</personaId>");
                sb.Append("<raceTickets>0</raceTickets><rating>0</rating>");
                sb.Append("<rep>").Append(N(p.Rep)).Append("</rep><repAtCurrentLevel>0</repAtCurrentLevel>");
                sb.Append("<statusMessage>").Append(Util.EscapeXml(p.Motto)).Append("</statusMessage>");
                sb.Append("</ProfileData>");
                return sb.ToString();
            }

            StringBuilder lg = new StringBuilder(384);
            lg.Append("<ProfileData><Boost>0</Boost>");
            lg.Append("<Cash>").Append(N(p.Cash)).Append("</Cash>");
            lg.Append("<IconIndex>").Append(N(p.IconIndex)).Append("</IconIndex>");
            lg.Append("<Level>").Append(N(p.Level)).Append("</Level>");
            lg.Append("<Motto>").Append(Util.EscapeXml(p.Motto)).Append("</Motto>");
            lg.Append("<Name>").Append(Util.EscapeXml(p.Name)).Append("</Name>");
            lg.Append("<PercentToLevel>0.0</PercentToLevel>");
            lg.Append("<PersonaId>").Append(N(p.Id)).Append("</PersonaId>");
            lg.Append("<Rating>0.0</Rating>");
            lg.Append("<Rep>").Append(N(p.Rep)).Append("</Rep>");
            lg.Append("<RepAtCurrentLevel>0</RepAtCurrentLevel>");
            lg.Append("<Score>0</Score></ProfileData>");
            return lg.ToString();
        }

        public static string PersonaBase(Persona p, bool online)
        {
            StringBuilder sb = new StringBuilder(384);
            sb.Append("<PersonaBase xmlns=\"http://schemas.datacontract.org/2004/07/Victory.Service.Objects\"");
            sb.Append(" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">");
            sb.Append("<Badges/><IconIndex>").Append(N(p.IconIndex)).Append("</IconIndex>");
            sb.Append("<Level>").Append(N(p.Level)).Append("</Level>");
            sb.Append("<Motto>").Append(Util.EscapeXml(p.Motto)).Append("</Motto>");
            sb.Append("<Name>").Append(Util.EscapeXml(p.Name)).Append("</Name>");
            sb.Append("<PersonaId>").Append(N(p.Id)).Append("</PersonaId>");
            sb.Append("<Presence>").Append(online ? "1" : "0").Append("</Presence>");
            sb.Append("<Score>0</Score>");
            sb.Append("<UserId>").Append(N(p.AccountId)).Append("</UserId>");
            sb.Append("</PersonaBase>");
            return sb.ToString();
        }

        public static string PersonaBaseList(List<Persona> personas)
        {
            StringBuilder sb = new StringBuilder(1024);
            sb.Append("<ArrayOfPersonaBase xmlns=\"http://schemas.datacontract.org/2004/07/Victory.Service.Objects\"");
            sb.Append(" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">");
            for (int i = 0; i < personas.Count; i++) sb.Append(PersonaBase(personas[i], false));
            sb.Append("</ArrayOfPersonaBase>");
            return sb.ToString();
        }

        public static string UserInfo(List<Persona> personas, long accountId, string securityToken, ClientProfile profile)
        {
            StringBuilder items = new StringBuilder(2048);
            for (int i = 0; i < personas.Count; i++) items.Append(ProfileData(personas[i], profile));

            Dictionary<string, string> v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            v["PersonaItems"] = items.ToString();
            v["UserId"] = N(accountId);
            v["SecurityToken"] = Util.EscapeXml(securityToken);
            v["DefaultPersonaIdx"] = "0";

            string tpl = Template(profile, "UserInfo");
            if (!string.IsNullOrEmpty(tpl)) return Fill(tpl, v);

            StringBuilder sb = new StringBuilder(4096);
            sb.Append("<UserInfo xmlns=\"http://schemas.datacontract.org/2004/07/Victory.TransferObjects.User\"");
            sb.Append(" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">");
            sb.Append("<defaultPersonaIdx>0</defaultPersonaIdx><personas>");
            sb.Append(items);
            sb.Append("</personas><user>");
            sb.Append("<fullGameAccess>true</fullGameAccess><isComplete>true</isComplete>");
            sb.Append("<remoteUserId>").Append(N(accountId)).Append("</remoteUserId>");
            sb.Append("<securityToken>").Append(Util.EscapeXml(securityToken)).Append("</securityToken>");
            sb.Append("<subscribeMsg>false</subscribeMsg>");
            sb.Append("<userId>").Append(N(accountId)).Append("</userId>");
            sb.Append("</user></UserInfo>");
            return sb.ToString();
        }

        // ----------------------------------------------------------------- cars

        /// <summary>Rewrites the Id inside a stored &lt;CustomCar&gt; payload.</summary>
        public static string OwnedCar(Car car)
        {
            StringBuilder sb = new StringBuilder(2048);
            string inner = car.CustomXml;
            if (!string.IsNullOrEmpty(inner))
            {
                if (inner.IndexOf("<OwnedCarTrans", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // stored as a whole OwnedCarTrans element already
                    return ReplaceFirst(inner, "<Id>" + N(car.Id) + "</Id>", "Id");
                }
                inner = ReplaceFirst(inner, "<Id>" + N(car.Id) + "</Id>", "Id");
                sb.Append("<OwnedCarTrans><CustomCar>").Append(inner).Append("</CustomCar>");
                sb.Append("<Durability>").Append(N(car.Durability)).Append("</Durability>");
                sb.Append("<Heat>1</Heat><Id>").Append(N(car.Id)).Append("</Id>");
                sb.Append("<OwnershipType>").Append(car.IsPreset ? "PresetCar" : "CustomizedCar").Append("</OwnershipType>");
                sb.Append("</OwnedCarTrans>");
                return sb.ToString();
            }

            sb.Append("<OwnedCarTrans xmlns=\"http://schemas.datacontract.org/2004/07/Victory.DataLayer.Serialization\"");
            sb.Append(" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">");
            sb.Append("<CustomCar><BaseCar>461323600</BaseCar><Durability>").Append(N(car.Durability)).Append("</Durability>");
            sb.Append("<Heat>1.0</Heat><Id>").Append(N(car.Id)).Append("</Id>");
            sb.Append("<IsPreset>").Append(car.IsPreset ? "true" : "false").Append("</IsPreset>");
            sb.Append("<Level>0</Level><Name>").Append(Util.EscapeXml(car.Name)).Append("</Name>");
            sb.Append("<Paints/><PerformanceParts/><PhysicsProfile><AttribHash>1309171635</AttribHash><Tier>3</Tier></PhysicsProfile>");
            sb.Append("<Rating>0</Rating><ResalePrice>0</ResalePrice><Vinyls/><VisualParts/></CustomCar>");
            sb.Append("<Durability>").Append(N(car.Durability)).Append("</Durability><Heat>1</Heat>");
            sb.Append("<Id>").Append(N(car.Id)).Append("</Id>");
            sb.Append("<OwnershipType>").Append(car.IsPreset ? "PresetCar" : "CustomizedCar").Append("</OwnershipType>");
            sb.Append("</OwnedCarTrans>");
            return sb.ToString();
        }

        /// <summary>Replaces the value of the first tag with this name, or inserts it.</summary>
        public static string ReplaceFirst(string xml, string replacement, string tagName)
        {
            if (string.IsNullOrEmpty(xml)) return xml;
            int open = xml.IndexOf("<" + tagName + ">", StringComparison.OrdinalIgnoreCase);
            if (open < 0)
            {
                int idx = xml.IndexOf("<CustomCar>", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return xml;
                return xml.Insert(idx + "<CustomCar>".Length, replacement);
            }
            int close = xml.IndexOf("</" + tagName + ">", open, StringComparison.OrdinalIgnoreCase);
            if (close < 0) return xml;
            int end = close + tagName.Length + 3;
            return xml.Substring(0, open) + replacement + xml.Substring(end);
        }

        public static string CarSlotInfo(Persona p)
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("<CarSlotInfoTrans>");
            sb.Append("<CarsOwnedByPersona>");
            for (int i = 0; i < p.Cars.Count; i++) sb.Append(OwnedCar(p.Cars[i]));
            sb.Append("</CarsOwnedByPersona>");
            int idx = 0;
            for (int i = 0; i < p.Cars.Count; i++) if (p.Cars[i].Id == p.CurrentCarId) { idx = i; break; }
            sb.Append("<DefaultOwnedCarIndex>").Append(N(idx)).Append("</DefaultOwnedCarIndex>");
            sb.Append("<ObtainableSlots/><OwnedCarSlotsCount>").Append(N(p.CarSlots)).Append("</OwnedCarSlotsCount>");
            sb.Append("</CarSlotInfoTrans>");
            return sb.ToString();
        }

        // ------------------------------------------------------------ inventory

        public static string Inventory(Persona p)
        {
            StringBuilder sb = new StringBuilder(2048);
            sb.Append("<InventoryTrans xmlns=\"http://schemas.datacontract.org/2004/07/Victory.DataLayer.Serialization\"");
            sb.Append(" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">");
            sb.Append("<Capacity>100</Capacity><InventoryItems>");
            sb.Append(PowerupItem("emergencyevade", "-611661916", "NFSW-NA:25AB32F9", 146855928));
            sb.Append(PowerupItem("ready", "957701799", "NFSW-NA:61989DE5", 146855932));
            sb.Append(PowerupItem("shield", "-364944936", "NFSW-NA:7A90B56A", 146855933));
            sb.Append(PowerupItem("trafficmagnet", "125509666", "NFSW-NA:8BF8B85A", 146855934));
            sb.Append(PowerupItem("slingshot", "2236629", "NFSW-NA:9310400B", 146855935));
            sb.Append(PowerupItem("juggernaut", "1805681994", "NFSW-NA:9CD438CC", 146855936));
            sb.Append(PowerupItem("onemorelap", "1627606782", "NFSW-NA:C53182C8", 146855938));
            sb.Append(PowerupItem("nosshot", "-1681514783", "NFSW-NA:E474C12F", 146855940));
            sb.Append("</InventoryItems><UsedSlotCount>0</UsedSlotCount></InventoryTrans>");
            return sb.ToString();
        }

        private static string PowerupItem(string tag, string hash, string productId, long invId)
        {
            return "<InventoryItemTrans><EntitlementTag>" + tag + "</EntitlementTag>" +
                   "<ExpirationDate i:nil=\"true\"/><Hash>" + hash + "</Hash>" +
                   "<InventoryId>" + N(invId) + "</InventoryId>" +
                   "<ProductId>" + productId + "</ProductId>" +
                   "<RemainingUseCount>100000</RemainingUseCount><ResellPrice>0.00000</ResellPrice>" +
                   "<Status>ACTIVE</Status><VirtualItemType>powerup</VirtualItemType></InventoryItemTrans>";
        }

        // ----------------------------------------------------------- misc docs

        public static string TreasureHunt()
        {
            return "<TreasureHuntEventSession xmlns=\"http://schemas.datacontract.org/2004/07/Victory.DataLayer.Serialization.Event\" " +
                   "xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"><CoinsCollected>0</CoinsCollected>" +
                   "<IsStreakBroken>false</IsStreakBroken><NumCoins>30</NumCoins><Seed>4</Seed><Streak>0</Streak>" +
                   "</TreasureHuntEventSession>";
        }

        public static string Achievements(Persona p)
        {
            StringBuilder sb = new StringBuilder(1024);
            sb.Append("<AchievementsPacket><Badges/><Definitions>");
            for (int i = 1; i <= 30; i++)
            {
                sb.Append("<AchievementDefinitionPacket><AchievementDefinitionId>").Append(N(i)).Append("</AchievementDefinitionId>");
                sb.Append("<AchievementRanks><AchievementRankPacket><AchievedOn/><AchievementRankId>").Append(N(i)).Append("</AchievementRankId>");
                sb.Append("<IsRare>false</IsRare><Points/><Rank>1</Rank><Rarity>0</Rarity><State>Inactive</State>");
                sb.Append("<ThresholdValue>").Append(N(100 * i)).Append("</ThresholdValue></AchievementRankPacket></AchievementRanks>");
                sb.Append("<BadgeDefinitionId>").Append(N(i)).Append("</BadgeDefinitionId><IsVisible>false</IsVisible>");
                sb.Append("</AchievementDefinitionPacket>");
            }
            sb.Append("</Definitions><PersonaId>").Append(N(p == null ? 0 : p.Id)).Append("</PersonaId></AchievementsPacket>");
            return sb.ToString();
        }

        public static string NewsArticles(string baseUrl, string profileId)
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append("<ArrayOfNewsArticleTrans><NewsArticleTrans>");
            sb.Append("<NewsArticleType>1</NewsArticleType>");
            sb.Append("<Title>DPT.Server</Title>");
            sb.Append("<Body>Welcome to Need For Speed World on DPT.Server. Have fun and drive safe.</Body>");
            sb.Append("<ImageUrl>").Append(Util.EscapeXml(Util.UrlJoin(baseUrl, "/Engine.svc/news/news1.png"))).Append("</ImageUrl>");
            sb.Append("<PublishDate>").Append(Util.Iso(DateTime.UtcNow.AddDays(-1))).Append("</PublishDate>");
            sb.Append("<ExpiryDate>").Append(Util.Iso(DateTime.UtcNow.AddYears(5))).Append("</ExpiryDate>");
            sb.Append("</NewsArticleTrans></ArrayOfNewsArticleTrans>");
            return sb.ToString();
        }

        /// <summary>Response for /GetServerInformation (JSON, modern builds only).</summary>
        public static string ServerInformation(string xmppHost, int xmppPort, string profileId)
        {
            JsonBuilder j = new JsonBuilder(256);
            j.BeginObject();
            j.Prop("xmppIp", xmppHost);
            j.Prop("xmppPort", (long)xmppPort);
            j.Prop("debug", false);
            j.Prop("gzip", false);
            j.Prop("commerceEnabled", true);
            j.Prop("httpPort", 0);
            j.Prop("xmppServerType", "offline");
            j.Prop("serverProfile", profileId);
            j.EndObject();
            return j.ToString();
        }
    }
}