﻿using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Hooks;
using Mono.Data.Sqlite;
using MySql.Data.MySqlClient;
using Terraria;
using TShockAPI;
using TShockAPI.DB;
using System.ComponentModel;

namespace InfiniteChests
{
    [APIVersion(1, 12)]
    public class InfiniteChests : TerrariaPlugin
    {
        public override string Author
        {
            get { return "MarioE"; }
        }
        public IDbConnection Database;
        public override string Description
        {
            get { return "Allows for infinite chests, and supports all chest control commands."; }
        }
        public PlayerInfo[] infos = new PlayerInfo[256];
        public override string Name
        {
            get { return "InfiniteChests"; }
        }
        public Dictionary<Point, int> Timer = new Dictionary<Point, int>();
        public System.Timers.Timer TimerDec = new System.Timers.Timer(1000);
        public override Version Version
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version; }
        }

        public InfiniteChests(Main game)
            : base(game)
        {
            Order = -1;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                NetHooks.GetData -= OnGetData;
                GameHooks.Initialize -= OnInitialize;
                ServerHooks.Leave -= OnLeave;

                Database.Dispose();
                TimerDec.Dispose();
            }
        }
        public override void Initialize()
        {
            NetHooks.GetData += OnGetData;
            GameHooks.Initialize += OnInitialize;
            ServerHooks.Leave += OnLeave;

            TimerDec.Elapsed += OnElapsed;
            TimerDec.Start();
        }

        void OnGetData(GetDataEventArgs e)
        {
            if (!e.Handled)
            {
                int index = e.Msg.whoAmI;
                switch (e.MsgID)
                {
                    case PacketTypes.ChestGetContents:
                        {
                            int X = BitConverter.ToInt32(e.Msg.readBuffer, e.Index);
                            int Y = BitConverter.ToInt32(e.Msg.readBuffer, e.Index + 4);
                            GetChest(X, Y, index);
                            e.Handled = true;
                        }
                        break;
                    case PacketTypes.ChestItem:
                        {
                            byte slot = e.Msg.readBuffer[e.Index + 2];
                            if (slot > 20)
                            {
                                return;
                            }
                            byte stack = e.Msg.readBuffer[e.Index + 3];
                            byte prefix = e.Msg.readBuffer[e.Index + 4];
                            int netID = BitConverter.ToInt16(e.Msg.readBuffer, e.Index + 5);
                            ModChest(index, slot, netID, stack, prefix);
                            e.Handled = true;
                        }
                        break;
                    case PacketTypes.Tile:
                        {
                            if (e.Msg.readBuffer[e.Index] == 1 && e.Msg.readBuffer[e.Index + 9] == 21)
                            {
                                int X = BitConverter.ToInt32(e.Msg.readBuffer, e.Index + 1);
                                int Y = BitConverter.ToInt32(e.Msg.readBuffer, e.Index + 5);
                                if (TShock.Regions.CanBuild(X, Y, TShock.Players[index]))
                                {
                                    PlaceChest(X, Y, index);
                                    WorldGen.PlaceChest(X, Y, 21, false, e.Msg.readBuffer[e.Index + 10]);
                                    NetMessage.SendData((int)PacketTypes.Tile, -1, index, "", 1, X, Y, 21, e.Msg.readBuffer[e.Index + 10]);
                                    e.Handled = true;
                                }
                            }
                        }
                        break;
                    case PacketTypes.TileKill:
                        {
                            int X = BitConverter.ToInt32(e.Msg.readBuffer, e.Index);
                            int Y = BitConverter.ToInt32(e.Msg.readBuffer, e.Index + 4);
                            if (TShock.Regions.CanBuild(X, Y, TShock.Players[index]) && Main.tile[X, Y].type == 21)
                            {
                                if (Main.tile[X, Y].frameY != 0)
                                {
                                    Y--;
                                }
                                if (Main.tile[X, Y].frameX % 36 != 0)
                                {
                                    X--;
                                }
                                KillChest(X, Y, index);
                                TShock.Players[index].SendTileSquare(X, Y, 3);
                                e.Handled = true;
                            }
                        }
                        break;
                }
            }
        }
        void OnElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            List<Point> dec = new List<Point>();
            foreach (Point p in Timer.Keys)
            {
                dec.Add(p);
            }
            foreach (Point p in dec)
            {
                if (Timer[p] == 0)
                {
                    Timer.Remove(p);
                }
                else
                {
                    Timer[p]--;
                }
            }
        }
        void OnInitialize()
        {
            Commands.ChatCommands.Add(new Command("protectchest", Deselect, "ccset"));
            Commands.ChatCommands.Add(new Command("showchestinfo", Info, "cinfo"));
            Commands.ChatCommands.Add(new Command("maintenance", ConvertChests, "convchests"));
            Commands.ChatCommands.Add(new Command("protectchest", PublicProtect, "cpset"));
            Commands.ChatCommands.Add(new Command("refillchest", Refill, "crefill"));
            Commands.ChatCommands.Add(new Command("protectchest", RegionProtect, "crset"));
            Commands.ChatCommands.Add(new Command("protectchest", Protect, "cset"));
            Commands.ChatCommands.Add(new Command("protectchest", Unprotect, "cunset"));

            switch (TShock.Config.StorageType.ToLower())
            {
                case "mysql":
                    string[] host = TShock.Config.MySqlHost.Split(':');
                    Database = new MySqlConnection()
                    {
                        ConnectionString = string.Format("Server={0}; Port={1}; Database={2}; Uid={3}; Pwd={4};",
                            host[0],
                            host.Length == 1 ? "3306" : host[1],
                            TShock.Config.MySqlDbName,
                            TShock.Config.MySqlUsername,
                            TShock.Config.MySqlPassword)
                    };
                    break;
                case "sqlite":
                    string sql = Path.Combine(TShock.SavePath, "chests.sqlite");
                    Database = new SqliteConnection(string.Format("uri=file://{0},Version=3", sql));
                    break;
            }
            SqlTableCreator sqlcreator = new SqlTableCreator(Database,
                Database.GetSqlType() == SqlType.Sqlite ? (IQueryBuilder)new SqliteQueryCreator() : new MysqlQueryCreator());
            sqlcreator.EnsureExists(new SqlTable("Chests",
                new SqlColumn("X", MySqlDbType.Int32),
                new SqlColumn("Y", MySqlDbType.Int32),
                new SqlColumn("Account", MySqlDbType.Text),
                new SqlColumn("Items", MySqlDbType.Text),
                new SqlColumn("Flags", MySqlDbType.Int32),
                new SqlColumn("WorldID", MySqlDbType.Int32)));
        }
        void OnLeave(int index)
        {
            infos[index] = new PlayerInfo();
        }

        void GetChest(int X, int Y, int plr)
        {
            Chest chest = null;
            using (QueryResult reader = Database.QueryReader("SELECT Account, Flags, Items FROM Chests WHERE X = @0 AND Y = @1 and WorldID = @2",
                X, Y, Main.worldID))
            {
                if (reader.Read())
                {
                    chest = new Chest
                    {
                        account = reader.Get<string>("Account"),
                        flags = (ChestFlags)reader.Get<int>("Flags"),
                        items = reader.Get<string>("Items")
                    };
                }
            }
            TSPlayer player = TShock.Players[plr];

            if (chest != null)
            {
                switch (infos[plr].action)
                {
                    case ChestAction.INFO:
                        player.SendMessage(string.Format("X: {0} Y: {1} Account: {2} {3}Refill: {4} ({5} second{6}) Region: {7}",
                            X, Y, chest.account == "" ? "N/A" : chest.account, ((chest.flags & ChestFlags.PUBLIC) != 0) ? "(public) " : "",
                            (chest.flags & ChestFlags.REFILL) != 0, (int)chest.flags / 8, (int)chest.flags / 8 == 1 ? "" : "s",
                            (chest.flags & ChestFlags.REGION) != 0), Color.Yellow);
                        break;
                    case ChestAction.PROTECT:
                        if (chest.account != "")
                        {
                            player.SendMessage("This chest is already protected.", Color.Red);
                            break;
                        }
                        Database.Query("UPDATE Chests SET Account = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
                            player.UserAccountName, X, Y, Main.worldID);
                        player.SendMessage("This chest is now protected.");
                        break;
                    case ChestAction.PUBLIC:
                        if (chest.account == "")
                        {
                            player.SendMessage("This chest is not protected.", Color.Red);
                            break;
                        }
                        if (chest.account != player.UserAccountName)
                        {
                            player.SendMessage("This chest is not yours.", Color.Red);
                            break;
                        }
                        Database.Query("UPDATE Chests SET Flags = ((~(Flags & 1)) & (Flags | 1)) WHERE X = @0 AND Y = @1 AND WorldID = @2",
                            X, Y, Main.worldID);
                        player.SendMessage(string.Format("This chest is now {0}.", (chest.flags & ChestFlags.PUBLIC) != 0 ? "private" : "public"));
                        break;
                    case ChestAction.REFILL:
                        if (chest.account != player.UserAccountName && chest.account != "")
                        {
                            player.SendMessage("This chest is not yours.", Color.Red);
                            break;
                        }
                        if (infos[plr].time > 0)
                        {
                            Database.Query("UPDATE Chests SET Flags = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
                                ((int)chest.flags & 3) + (infos[plr].time * 8) + 4, X, Y, Main.worldID);
                            player.SendMessage(string.Format("This chest will now refill with a delay of {0} second{1}.", infos[plr].time,
                                infos[plr].time == 1 ? "" : "s"));
                        }
                        else
                        {
                            Database.Query("UPDATE Chests SET Flags = ((~(Flags & 4)) & (Flags | 4)) & 7 WHERE X = @0 AND Y = @1 AND WorldID = @2",
                                X, Y, Main.worldID);
                            player.SendMessage(string.Format("This chest will {0} refill.", (chest.flags & ChestFlags.REFILL) != 0 ? "no longer" : "now"));
                        }
                        break;
                    case ChestAction.REGION:
                        if (chest.account == "")
                        {
                            player.SendMessage("This chest is not protected.", Color.Red);
                            break;
                        }
                        if (chest.account != player.UserAccountName)
                        {
                            player.SendMessage("This chest is not yours.", Color.Red);
                            break;
                        }
                        Database.Query("UPDATE Chests SET Flags = ((~(Flags & 2)) & (Flags | 2)) WHERE X = @0 AND Y = @1 AND WorldID = @2",
                            X, Y, Main.worldID);
                        player.SendMessage(string.Format("This chest is {0} region shared.", (chest.flags & ChestFlags.REGION) != 0 ? "no longer" : "now"));
                        break;
                    case ChestAction.UNPROTECT:
                        if (chest.account == "")
                        {
                            player.SendMessage("This chest is not protected.", Color.Red);
                            break;
                        }
                        if (chest.account != player.UserAccountName && !player.Group.HasPermission("removechestprotection"))
                        {
                            player.SendMessage("This chest is not yours.", Color.Red);
                            break;
                        }
                        Database.Query("UPDATE Chests SET Account = '' WHERE X = @0 AND Y = @1 AND WorldID = @2",
                            X, Y, Main.worldID);
                        player.SendMessage("This chest is now un-protected.");
                        break;
                    default:
                        if ((chest.flags & ChestFlags.PUBLIC) == 0 && ((chest.account != player.UserAccountName &&
                            chest.account != "" && !player.Group.HasPermission("openallchests") && (chest.flags & ChestFlags.REGION) == 0)
                            || ((chest.flags & ChestFlags.REGION) != 0 && !TShock.Regions.CanBuild(X, Y, player))))
                        {
                            player.SendMessage("This chest is protected.", Color.Red);
                            break;
                        }
                        int timeLeft;
                        if (Timer.TryGetValue(new Point(X, Y), out timeLeft) && timeLeft > 0)
                        {
                            player.SendMessage(string.Format("This chest will refill in {0} second{1}.", timeLeft, timeLeft == 1 ? "" : "s"), Color.Red);
                            break;
                        }
                        int[] itemArgs = new int[60];
                        string[] split = chest.items.Split(',');
                        for (int i = 0; i < 60; i++)
                        {
                            itemArgs[i] = Convert.ToInt32(split[i]);
                        }
                        byte[] raw = new byte[] { 8, 0, 0, 0, 32, 0, 0, 255, 255, 255, 255, 255 };
                        for (int i = 0; i < 20; i++)
                        {
                            raw[7] = (byte)i;
                            raw[8] = (byte)itemArgs[i * 3 + 1];
                            raw[9] = (byte)itemArgs[i * 3 + 2];
                            raw[10] = (byte)itemArgs[i * 3];
                            raw[11] = (byte)(itemArgs[i * 3] >> 8);
                            player.SendRawData(raw);
                        }
                        byte[] raw2 = new byte[] { 11, 0, 0, 0, 33, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255 };
                        Buffer.BlockCopy(BitConverter.GetBytes(X), 0, raw2, 7, 4);
                        Buffer.BlockCopy(BitConverter.GetBytes(Y), 0, raw2, 11, 4);
                        player.SendRawData(raw2);
                        infos[plr].x = X;
                        infos[plr].y = Y;
                        break;
                }
                infos[plr].action = ChestAction.NONE;
            }
        }
        void KillChest(int X, int Y, int plr)
        {
            Chest chest = null;
            using (QueryResult reader = Database.QueryReader("SELECT Account, Items FROM Chests WHERE X = @0 AND Y = @1 AND WorldID = @2",
                X, Y, Main.worldID))
            {
                if (reader.Read())
                {
                    chest = new Chest { account = reader.Get<string>("Account"), items = reader.Get<string>("Items") };
                }
            }
            TSPlayer player = TShock.Players[plr];

            if (chest != null && chest.account != player.UserAccountName && chest.account != "")
            {
                player.SendMessage("This chest is protected.", Color.Red);
                player.SendTileSquare(X, Y, 3);
            }
            else if (chest != null && chest.items !=
                "0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0")
            {
                player.SendTileSquare(X, Y, 3);
            }
            else
            {
                WorldGen.KillTile(X, Y);
                Database.Query("DELETE FROM Chests WHERE X = @0 AND Y = @1 and WorldID = @2", X, Y, Main.worldID);
                TSPlayer.All.SendData(PacketTypes.Tile, "", 0, X, Y + 1);
            }
        }
        void ModChest(int plr, byte slot, int ID, byte stack, byte prefix)
        {
            Chest chest = null;
            using (QueryResult reader = Database.QueryReader("SELECT Flags, Items FROM Chests WHERE X = @0 AND Y = @1 AND WorldID = @2",
                infos[plr].x, infos[plr].y, Main.worldID))
            {
                if (reader.Read())
                {
                    chest = new Chest { flags = (ChestFlags)reader.Get<int>("Flags"), items = reader.Get<string>("Items") };
                }
            }
            TSPlayer player = TShock.Players[plr];

            if (chest != null)
            {
                if ((chest.flags & ChestFlags.REFILL) != 0)
                {
                    if (!Timer.ContainsKey(new Point(infos[plr].x, infos[plr].y)))
                    {
                        Timer.Add(new Point(infos[plr].x, infos[plr].y), (int)chest.flags >> 3);
                    }
                }
                else
                {
                    int[] itemArgs = new int[60];
                    string[] split = chest.items.Split(',');
                    for (int i = 0; i < 60; i++)
                    {
                        itemArgs[i] = Convert.ToInt32(split[i]);
                    }
                    itemArgs[slot * 3] = ID;
                    itemArgs[slot * 3 + 1] = stack;
                    itemArgs[slot * 3 + 2] = prefix;
                    StringBuilder newItems = new StringBuilder();
                    for (int i = 0; i < 60; i++)
                    {
                        newItems.Append(itemArgs[i]);
                        if (i != 59)
                        {
                            newItems.Append(',');
                        }
                    }
                    Database.Query("UPDATE Chests SET Items = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
                        newItems.ToString(), infos[plr].x, infos[plr].y, Main.worldID);

                    for (int i = 0; i < 256; i++)
                    {
                        if (infos[i].x == infos[plr].x && infos[i].y == infos[plr].y && i != plr)
                        {
                            byte[] raw = new byte[] { 8, 0, 0, 0, 32, 0, 0, slot, stack, prefix, (byte)ID, (byte)(ID >> 8) };
                            TShock.Players[i].SendRawData(raw);
                        }
                    }
                }
            }
        }
        void PlaceChest(int X, int Y, int plr)
        {
            TSPlayer player = TShock.Players[plr];
            Database.Query("INSERT INTO Chests (X, Y, Account, Flags, Items, WorldID) VALUES (@0, @1, @2, @3, @4, @5)",
                X, Y - 1, player.IsLoggedIn ? player.UserAccountName : "", 0,
                "0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0", Main.worldID);
            Main.chest[0] = null;
        }

        void ConvertChests(CommandArgs e)
        {
            Database.Query("DELETE FROM Chests WHERE WorldID = @0", Main.worldID);
            int converted = 0;
            StringBuilder items = new StringBuilder();
            for (int i = 0; i < 1000; i++)
            {
                Terraria.Chest c = Main.chest[i];
                if (c != null)
                {
                    for (int j = 0; j < 20; j++)
                    {
                        items.Append(c.item[j].netID + "," + c.item[j].stack + "," + c.item[j].prefix);
                        if (j != 20)
                        {
                            items.Append(",");
                        }
                    }
                    Database.Query("INSERT INTO Chests (X, Y, Account, Items, WorldID) VALUES (@0, @1, '', @2, @3)",
                        c.x, c.y, items.ToString(), Main.worldID);
                    converted++;
                    items.Clear();
                    Main.chest[i] = null;
                }
            }
            e.Player.SendMessage(string.Format("Converted {0} chest{1}.", converted, converted == 1 ? "" : "s"));
        }
        void Deselect(CommandArgs e)
        {
            infos[e.Player.Index].action = ChestAction.NONE;
            e.Player.SendMessage("Stopped selecting a chest.");
        }
        void Info(CommandArgs e)
        {
            infos[e.Player.Index].action = ChestAction.INFO;
            e.Player.SendMessage("Open a chest to get its info.");
        }
        void Protect(CommandArgs e)
        {
            infos[e.Player.Index].action = ChestAction.PROTECT;
            e.Player.SendMessage("Open a chest to protect it.");
        }
        void Refill(CommandArgs e)
        {
            if (e.Parameters.Count > 1)
            {
                e.Player.SendMessage("Syntax: /crefill [<interval>]", Color.Red);
                return;
            }
            infos[e.Player.Index].time = 0;
            if (e.Parameters.Count == 1)
            {
                int time;
                if (int.TryParse(e.Parameters[0], out time) && time > 0)
                {
                    infos[e.Player.Index].action = ChestAction.REFILL;
                    infos[e.Player.Index].time = time;
                    e.Player.SendMessage(string.Format("Open a chest to make it refill with an interval of {0} second{1}.", time,
                        time == 1 ? "" : "s"));
                    return;
                }
                e.Player.SendMessage("Invalid interval!", Color.Red);
            }
            else
            {
                infos[e.Player.Index].action = ChestAction.REFILL;
                e.Player.SendMessage("Open a chest to toggle its refill status.");
            }
        }
        void PublicProtect(CommandArgs e)
        {
            infos[e.Player.Index].action = ChestAction.PUBLIC;
            e.Player.SendMessage("Open a chest to toggle its public status.");
        }
        void RegionProtect(CommandArgs e)
        {
            infos[e.Player.Index].action = ChestAction.REGION;
            e.Player.SendMessage("Open a chest to toggle its region share status.");
        }
        void Unprotect(CommandArgs e)
        {
            infos[e.Player.Index].action = ChestAction.UNPROTECT;
            e.Player.SendMessage("Open a chest to unprotect it.");
        }
    }
}
