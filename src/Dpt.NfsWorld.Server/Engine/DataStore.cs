using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Dpt.NfsWorld.Core;

namespace Dpt.NfsWorld.Engine
{
    public sealed class Car
    {
        public long Id;
        public long PersonaId;
        public string ProductId = "";
        public string Name = "";
        public string PresetHash = "";
        public int Durability = 100;
        public bool IsPreset;
        public DateTime AcquiredUtc = DateTime.UtcNow;
        /// <summary>Original &lt;CustomCar&gt; payload from the profile basket (preset rides).</summary>
        public string CustomXml = "";

        public Dictionary<string, object> ToJson()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["id"] = Id;
            d["personaId"] = PersonaId;
            d["productId"] = ProductId;
            d["name"] = Name;
            d["presetHash"] = PresetHash;
            d["durability"] = (long)Durability;
            d["isPreset"] = IsPreset;
            d["acquiredUtc"] = Util.Iso(AcquiredUtc);
            if (!string.IsNullOrEmpty(CustomXml)) d["customXml"] = CustomXml;
            return d;
        }

        public static Car FromJson(Dictionary<string, object> d)
        {
            Car c = new Car();
            c.Id = Json.GetLong(d, "id", 0);
            c.PersonaId = Json.GetLong(d, "personaId", 0);
            c.ProductId = Json.GetString(d, "productId", "");
            c.Name = Json.GetString(d, "name", "");
            c.PresetHash = Json.GetString(d, "presetHash", "");
            c.Durability = Json.GetInt(d, "durability", 100);
            c.IsPreset = Json.GetBool(d, "isPreset", false);
            c.AcquiredUtc = ParseDate(Json.GetString(d, "acquiredUtc", null));
            c.CustomXml = Json.GetString(d, "customXml", "");
            return c;
        }

        internal static DateTime ParseDate(string s)
        {
            if (string.IsNullOrEmpty(s)) return DateTime.UtcNow;
            DateTime dt;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                return dt;
            return DateTime.UtcNow;
        }
    }

    public sealed class Persona
    {
        public long Id;
        public long AccountId;
        public string Name = "";
        public string Motto = "";
        public int IconIndex = 0;
        public int Level = 1;
        public long Rep;
        public long Cash;
        public int Tokens;
        public int CarSlots = 1;
        public long CurrentCarId;
        public DateTime CreatedUtc = DateTime.UtcNow;
        public DateTime LastSeenUtc = DateTime.UtcNow;
        public List<Car> Cars = new List<Car>();

        public Car CurrentCar
        {
            get
            {
                for (int i = 0; i < Cars.Count; i++) if (Cars[i].Id == CurrentCarId) return Cars[i];
                return Cars.Count > 0 ? Cars[0] : null;
            }
        }

        public Dictionary<string, object> ToJson()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["id"] = Id;
            d["accountId"] = AccountId;
            d["name"] = Name;
            d["motto"] = Motto;
            d["iconIndex"] = (long)IconIndex;
            d["level"] = (long)Level;
            d["rep"] = Rep;
            d["cash"] = Cash;
            d["tokens"] = (long)Tokens;
            d["carSlots"] = (long)CarSlots;
            d["currentCarId"] = CurrentCarId;
            d["createdUtc"] = Util.Iso(CreatedUtc);
            d["lastSeenUtc"] = Util.Iso(LastSeenUtc);
            List<object> cars = new List<object>();
            for (int i = 0; i < Cars.Count; i++) cars.Add(Cars[i].ToJson());
            d["cars"] = cars;
            return d;
        }

        public static Persona FromJson(Dictionary<string, object> d)
        {
            Persona p = new Persona();
            p.Id = Json.GetLong(d, "id", 0);
            p.AccountId = Json.GetLong(d, "accountId", 0);
            p.Name = Json.GetString(d, "name", "");
            p.Motto = Json.GetString(d, "motto", "");
            p.IconIndex = Json.GetInt(d, "iconIndex", 0);
            p.Level = Json.GetInt(d, "level", 1);
            p.Rep = Json.GetLong(d, "rep", 0);
            p.Cash = Json.GetLong(d, "cash", 0);
            p.Tokens = Json.GetInt(d, "tokens", 0);
            p.CarSlots = Json.GetInt(d, "carSlots", 1);
            p.CurrentCarId = Json.GetLong(d, "currentCarId", 0);
            p.CreatedUtc = Car.ParseDate(Json.GetString(d, "createdUtc", null));
            p.LastSeenUtc = Car.ParseDate(Json.GetString(d, "lastSeenUtc", null));
            List<object> cars = Json.Get(d, "cars") as List<object>;
            if (cars != null)
            {
                for (int i = 0; i < cars.Count; i++)
                {
                    Dictionary<string, object> cd = cars[i] as Dictionary<string, object>;
                    if (cd != null) p.Cars.Add(Car.FromJson(cd));
                }
            }
            return p;
        }
    }

    public sealed class Account
    {
        public long Id;
        public string Username = "";
        public string Salt = "";
        public string PassHash = "";
        public DateTime CreatedUtc = DateTime.UtcNow;
        public DateTime LastLoginUtc = DateTime.UtcNow;
        public bool Banned;
        public bool Admin;
        public List<long> PersonaIds = new List<long>();

        public Dictionary<string, object> ToJson()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["id"] = Id;
            d["username"] = Username;
            d["salt"] = Salt;
            d["passHash"] = PassHash;
            d["createdUtc"] = Util.Iso(CreatedUtc);
            d["lastLoginUtc"] = Util.Iso(LastLoginUtc);
            d["banned"] = Banned;
            d["admin"] = Admin;
            List<object> ids = new List<object>();
            for (int i = 0; i < PersonaIds.Count; i++) ids.Add(PersonaIds[i]);
            d["personaIds"] = ids;
            return d;
        }

        public static Account FromJson(Dictionary<string, object> d)
        {
            Account a = new Account();
            a.Id = Json.GetLong(d, "id", 0);
            a.Username = Json.GetString(d, "username", "");
            a.Salt = Json.GetString(d, "salt", "");
            a.PassHash = Json.GetString(d, "passHash", "");
            a.CreatedUtc = Car.ParseDate(Json.GetString(d, "createdUtc", null));
            a.LastLoginUtc = Car.ParseDate(Json.GetString(d, "lastLoginUtc", null));
            a.Banned = Json.GetBool(d, "banned", false);
            a.Admin = Json.GetBool(d, "admin", false);
            List<object> ids = Json.Get(d, "personaIds") as List<object>;
            if (ids != null)
                for (int i = 0; i < ids.Count; i++)
                    if (ids[i] is long) a.PersonaIds.Add((long)ids[i]);
            return a;
        }
    }

    /// <summary>
    /// Everything the game needs to remember: accounts, personas, cars, wallets.
    /// Stored as one JSON document under data/state/db.json, written atomically and
    /// debounced (game traffic writes a lot, disks do not like that).
    /// </summary>
    public sealed class DataStore
    {
        private readonly object _sync = new object();
        private readonly Config _cfg;
        private readonly string _path;
        private readonly List<Account> _accounts = new List<Account>();
        private readonly List<Persona> _personas = new List<Persona>();
        private long _nextAccountId = 1;
        private long _nextPersonaId = 1000;
        private long _nextCarId = 1;
        private bool _dirty;
        private DateTime _lastSave = DateTime.UtcNow;

        public DataStore(Config cfg)
        {
            _cfg = cfg;
            Directory.CreateDirectory(cfg.StatePath);
            _path = Path.Combine(cfg.StatePath, "db.json");
            Load();
        }

        public string Path_ { get { return _path; } }

        public int AccountCount { get { lock (_sync) { return _accounts.Count; } } }
        public int PersonaCount { get { lock (_sync) { return _personas.Count; } } }
        public int CarCount { get { lock (_sync) { int n = 0; for (int i = 0; i < _personas.Count; i++) n += _personas[i].Cars.Count; return n; } } }

        // ------------------------------------------------------------- storage

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                string text = File.ReadAllText(_path, Encoding.UTF8);
                Dictionary<string, object> root = Json.TryParseObject(text);
                if (root == null) { Log.Error("DB", "state file is not valid JSON, starting empty"); return; }

                _nextAccountId = Json.GetLong(root, "nextAccountId", 1);
                _nextPersonaId = Json.GetLong(root, "nextPersonaId", 1000);
                _nextCarId = Json.GetLong(root, "nextCarId", 1);

                List<object> accs = Json.Get(root, "accounts") as List<object>;
                if (accs != null)
                    for (int i = 0; i < accs.Count; i++)
                    {
                        Dictionary<string, object> d = accs[i] as Dictionary<string, object>;
                        if (d != null) _accounts.Add(Account.FromJson(d));
                    }
                List<object> pers = Json.Get(root, "personas") as List<object>;
                if (pers != null)
                    for (int i = 0; i < pers.Count; i++)
                    {
                        Dictionary<string, object> d = pers[i] as Dictionary<string, object>;
                        if (d != null) _personas.Add(Persona.FromJson(d));
                    }
                Log.Info("DB", "loaded " + _accounts.Count + " accounts, " + _personas.Count + " personas from " + _path);
            }
            catch (Exception ex)
            {
                Log.Error("DB", "cannot load state: " + ex.Message);
            }
        }

        public void Save()
        {
            lock (_sync)
            {
                if (!_dirty) return;
                _dirty = false;
            }
            try
            {
                Dictionary<string, object> root = new Dictionary<string, object>();
                lock (_sync)
                {
                    root["nextAccountId"] = _nextAccountId;
                    root["nextPersonaId"] = _nextPersonaId;
                    root["nextCarId"] = _nextCarId;
                    List<object> accs = new List<object>();
                    for (int i = 0; i < _accounts.Count; i++) accs.Add(_accounts[i].ToJson());
                    root["accounts"] = accs;
                    List<object> pers = new List<object>();
                    for (int i = 0; i < _personas.Count; i++) pers.Add(_personas[i].ToJson());
                    root["personas"] = pers;
                }
                string text = Json.Stringify(root);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
            }
            catch (Exception ex)
            {
                Log.Error("DB", "cannot save state: " + ex.Message);
            }
        }

        /// <summary>Called from the supervisor every few seconds.</summary>
        public void FlushIfDue()
        {
            bool due;
            lock (_sync)
            {
                due = _dirty && (DateTime.UtcNow - _lastSave).TotalSeconds >= 5;
                if (due) _lastSave = DateTime.UtcNow;
            }
            if (due) Save();
        }

        public void MarkDirty() { lock (_sync) _dirty = true; }

        // ------------------------------------------------------------ accounts

        public static string HashPassword(string password, string salt)
        {
            using (SHA256Managed sha = new SHA256Managed())
            {
                byte[] data = Encoding.UTF8.GetBytes((salt ?? "") + "|" + (password ?? ""));
                return Util.HexCompact(sha.ComputeHash(data));
            }
        }

        public Account FindAccountByName(string username)
        {
            if (string.IsNullOrEmpty(username)) return null;
            lock (_sync)
            {
                for (int i = 0; i < _accounts.Count; i++)
                    if (string.Equals(_accounts[i].Username, username, StringComparison.OrdinalIgnoreCase))
                        return _accounts[i];
                return null;
            }
        }

        public Account FindAccountById(long id)
        {
            lock (_sync)
            {
                for (int i = 0; i < _accounts.Count; i++) if (_accounts[i].Id == id) return _accounts[i];
                return null;
            }
        }

        public Account CreateAccount(string username, string password)
        {
            lock (_sync)
            {
                Account a = new Account();
                a.Id = _nextAccountId++;
                a.Username = string.IsNullOrEmpty(username) ? ("driver" + a.Id.ToString(CultureInfo.InvariantCulture)) : username.Trim();
                a.Salt = Util.ShortToken(8);
                a.PassHash = HashPassword(password, a.Salt);
                a.CreatedUtc = DateTime.UtcNow;
                a.LastLoginUtc = DateTime.UtcNow;
                _accounts.Add(a);
                _dirty = true;
                Log.Info("DB", "account created: " + a.Username + " (#" + a.Id + ")");
                return a;
            }
        }

        /// <summary>Returns the account when the credentials match, null otherwise.</summary>
        public Account Authenticate(string username, string password, bool createIfMissing, bool requirePassword)
        {
            Account a = FindAccountByName(username);
            if (a == null)
            {
                if (!createIfMissing) return null;
                a = CreateAccount(username, password);
                return a;
            }
            if (a.Banned) return null;
            if (requirePassword && !string.IsNullOrEmpty(a.PassHash))
            {
                if (!Util.FixedEquals(a.PassHash, HashPassword(password, a.Salt))) return null;
            }
            lock (_sync)
            {
                a.LastLoginUtc = DateTime.UtcNow;
                _dirty = true;
            }
            return a;
        }

        public void SetPassword(Account a, string password)
        {
            if (a == null) return;
            lock (_sync)
            {
                a.Salt = Util.ShortToken(8);
                a.PassHash = HashPassword(password, a.Salt);
                _dirty = true;
            }
        }

        public void SetBanned(Account a, bool banned)
        {
            if (a == null) return;
            lock (_sync) { a.Banned = banned; _dirty = true; }
        }

        public List<Account> AllAccounts()
        {
            lock (_sync) return new List<Account>(_accounts);
        }

        // ------------------------------------------------------------ personas

        public Persona FindPersona(long id)
        {
            lock (_sync)
            {
                for (int i = 0; i < _personas.Count; i++) if (_personas[i].Id == id) return _personas[i];
                return null;
            }
        }

        public Persona FindPersonaByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (_sync)
            {
                for (int i = 0; i < _personas.Count; i++)
                    if (string.Equals(_personas[i].Name, name, StringComparison.OrdinalIgnoreCase))
                        return _personas[i];
                return null;
            }
        }

        public List<Persona> PersonasOf(Account a)
        {
            List<Persona> list = new List<Persona>();
            if (a == null) return list;
            lock (_sync)
            {
                for (int i = 0; i < _personas.Count; i++)
                    if (_personas[i].AccountId == a.Id) list.Add(_personas[i]);
            }
            return list;
        }

        public List<Persona> AllPersonas()
        {
            lock (_sync) return new List<Persona>(_personas);
        }

        public Persona CreatePersona(Account account, string name, int iconIndex)
        {
            if (account == null) return null;
            string clean = CleanPersonaName(name);
            if (clean.Length == 0) clean = "Driver";
            lock (_sync)
            {
                for (int i = 0; i < _personas.Count; i++)
                    if (string.Equals(_personas[i].Name, clean, StringComparison.OrdinalIgnoreCase))
                        return null; // name taken
                Persona p = new Persona();
                p.Id = _nextPersonaId++;
                p.AccountId = account.Id;
                p.Name = clean;
                p.IconIndex = Math.Max(0, iconIndex);
                p.Level = _cfg.StartingLevel;
                p.Cash = _cfg.StartingCash;
                p.Rep = _cfg.StartingRep;
                p.CarSlots = 1;
                p.CreatedUtc = DateTime.UtcNow;
                p.LastSeenUtc = DateTime.UtcNow;
                _personas.Add(p);
                account.PersonaIds.Add(p.Id);
                AddCarLocked(p, _cfg.StartingCar, "Starting Car", true);
                p.CurrentCarId = p.Cars[0].Id;
                _dirty = true;
                Log.Info("DB", "persona created: " + p.Name + " (#" + p.Id + ") for " + account.Username);
                return p;
            }
        }

        public static string CleanPersonaName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            StringBuilder sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length && sb.Length < 16; i++)
            {
                char c = name[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == ' ') sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        public bool DeletePersona(long personaId)
        {
            lock (_sync)
            {
                Persona p = null;
                for (int i = 0; i < _personas.Count; i++) if (_personas[i].Id == personaId) { p = _personas[i]; break; }
                if (p == null) return false;
                _personas.Remove(p);
                Account a = null;
                for (int i = 0; i < _accounts.Count; i++) if (_accounts[i].Id == p.AccountId) { a = _accounts[i]; break; }
                if (a != null) a.PersonaIds.Remove(personaId);
                _dirty = true;
                Log.Info("DB", "persona deleted: " + p.Name + " (#" + p.Id + ")");
                return true;
            }
        }

        public void TouchPersona(Persona p)
        {
            if (p == null) return;
            lock (_sync) { p.LastSeenUtc = DateTime.UtcNow; _dirty = true; }
        }

        public void AddCash(Persona p, long amount)
        {
            if (p == null) return;
            lock (_sync) { p.Cash = Math.Max(0, p.Cash + amount); _dirty = true; }
        }

        public bool SpendCash(Persona p, long amount)
        {
            if (p == null) return false;
            lock (_sync)
            {
                if (amount <= 0) return true;
                if (p.Cash < amount) return false;
                p.Cash -= amount;
                _dirty = true;
                return true;
            }
        }

        public void AddRep(Persona p, long amount)
        {
            if (p == null) return;
            lock (_sync)
            {
                p.Rep = Math.Max(0, p.Rep + amount);
                int level = LevelForRep(p.Rep);
                if (level > p.Level) p.Level = level;
                _dirty = true;
            }
        }

        /// <summary>Simple, monotonic level curve. Replaced by the profile map when available.</summary>
        public static int LevelForRep(long rep)
        {
            if (rep <= 0) return 1;
            int level = 1;
            long need = 500;
            while (rep >= need && level < 60)
            {
                level++;
                need += 250 + level * 250;
            }
            return level;
        }

        // ----------------------------------------------------------------- cars

        private Car AddCarLocked(Persona p, string productId, string name, bool isPreset)
        {
            Car c = new Car();
            c.Id = _nextCarId++;
            c.PersonaId = p.Id;
            c.ProductId = productId ?? "";
            c.Name = name ?? c.ProductId;
            c.IsPreset = isPreset;
            c.AcquiredUtc = DateTime.UtcNow;
            p.Cars.Add(c);
            _dirty = true;
            return c;
        }

        public Car AddCar(Persona p, string productId, string name, bool isPreset)
        {
            if (p == null) return null;
            lock (_sync) return AddCarLocked(p, productId, name, isPreset);
        }

        public Car FindCar(Persona p, long carId)
        {
            if (p == null) return null;
            lock (_sync)
            {
                for (int i = 0; i < p.Cars.Count; i++) if (p.Cars[i].Id == carId) return p.Cars[i];
                return null;
            }
        }

        public bool RemoveCar(Persona p, long carId)
        {
            if (p == null) return false;
            lock (_sync)
            {
                for (int i = 0; i < p.Cars.Count; i++)
                {
                    if (p.Cars[i].Id != carId) continue;
                    if (p.Cars.Count <= 1) return false;
                    p.Cars.RemoveAt(i);
                    if (p.CurrentCarId == carId) p.CurrentCarId = p.Cars[0].Id;
                    _dirty = true;
                    return true;
                }
                return false;
            }
        }

        public void SetCurrentCar(Persona p, long carId)
        {
            if (p == null) return;
            lock (_sync)
            {
                if (FindCar(p, carId) != null) { p.CurrentCarId = carId; _dirty = true; }
            }
        }
    }
}