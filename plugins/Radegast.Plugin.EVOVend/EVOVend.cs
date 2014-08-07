﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Radegast;
using OpenMetaverse;

using System.Text.RegularExpressions;
using System.Net;
using System.Linq;
using System.IO;

namespace Radegast.Plugin.Demo
{
    [Radegast.Plugin(Name="EVOVend Plugin", Description="EVO Vendor Delivery System", Version="1.0")]
    public class DemoPlugin : IRadegastPlugin
    {
        private System.Threading.Timer timer;
        private InventoryManager Manager;
        private OpenMetaverse.Inventory Inventory;

        private string vendURL = @"http://evosl.org/TREK/SL/index.php";
        List<InventoryBase> searchRes = new List<InventoryBase>();

        private RadegastInstance Instance;
        private GridClient Client { get { return Instance.Client; } }

        private string pluginName = "EVOVend";
        private string version = "1.0";

        public DemoPlugin ()
	    {
            
	    }

        public void StartPlugin(RadegastInstance inst)
        {
            Instance = inst;
            Instance.MainForm.TabConsole.DisplayNotificationInChat(pluginName + " version " + version + " loaded");

            // setup timer
            timer = new System.Threading.Timer(new TimerCallback(productCallback));
            timer.Change((30 * 1000), (60 * 1000));
            Instance.MainForm.TabConsole.DisplayNotificationInChat(pluginName + ":  Waiting 30 seconds for Inventory...");
        }

        public void StopPlugin(RadegastInstance instance)
        {
            // kill timer
            timer.Dispose();
        }

        private string m_searchString;
        public string searchString { 
            get 
            {
                return m_searchString;
            }
            set 
            {
                m_searchString = value;
                if(!String.IsNullOrEmpty(value))
                    PerformRecursiveSearch(0, Inventory.RootFolder.UUID);
            }
        }
        
        void PerformRecursiveSearch(int level, UUID folderID)
        {
            var me = Inventory.Items[folderID].Data;
            var sorted = Inventory.GetContents(folderID);

            sorted.Sort((InventoryBase b1, InventoryBase b2) =>
            {
                if (b1 is InventoryFolder && !(b2 is InventoryFolder))
                {
                    return -1;
                }
                else if (!(b1 is InventoryFolder) && b2 is InventoryFolder)
                {
                    return 1;
                }
                else
                {
                    return string.Compare(b1.Name, b2.Name);
                }
            });

            foreach (var item in sorted)
            {
                if (item is InventoryFolder)
                {
                    PerformRecursiveSearch(level + 1, item.UUID);
                }
                else
                {
                    var it = item as InventoryItem;

                    if (it.UUID.ToString().Contains(searchString))
                        searchRes.Add(it); 
                }
            }
        }

        class DeliveryQueue {
            public string ClassName { get; set; }
            public string id {get;set;}
            public string userUUID {get;set;}
            public string objectUUID {get;set;}
            public string price {get;set;}
            public string created { get; set; }
            public string delivered { get; set; }
        }

        private string RequestVendor(string action, Dictionary<string, string> param = null)
        {
            try
            {
                var webRequest = WebRequest.Create(this.vendURL);

                string postData = "action=" + action;
                if (param != null && param.Count > 0)
                {
                    var kv = param.Select(p => "&" + p.Key + "=" +p.Value);
                    postData += String.Join("", kv.ToArray());
                }
                byte[] byteArray = Encoding.UTF8.GetBytes(postData);

                webRequest.Method = "POST";
                webRequest.ContentType = "application/x-www-form-urlencoded";
                webRequest.ContentLength = byteArray.Length;

                // add post data to request
                Stream postStream = webRequest.GetRequestStream();
                postStream.Write(byteArray, 0, byteArray.Length);
                postStream.Flush();
                postStream.Close();

                using (var response = webRequest.GetResponse())
                using (var content = response.GetResponseStream())
                using (var reader = new System.IO.StreamReader(content))
                {
                    return reader.ReadToEnd();
                }
            }catch { }
            return null;
        }

        private List<DeliveryQueue> parseResponse(string content)
        {
            List<DeliveryQueue> queue = new List<DeliveryQueue>();

            if (String.IsNullOrEmpty(content)) return queue;

            System.Reflection.PropertyInfo[] propertyInfos = typeof(DeliveryQueue).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            string field_separator = "|";

            var lines = content.Split("\n".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
            foreach (string l in lines)
            {
                int lastPos = 0;

                var deliveryQ = new DeliveryQueue();
                foreach (System.Reflection.PropertyInfo pInfo in propertyInfos)
                {
                    var nextPos = l.IndexOf(field_separator, lastPos);
                    if(nextPos > -1){
                        pInfo.SetValue(deliveryQ, l.Substring(lastPos, nextPos - lastPos), null);
                    }
                    lastPos = nextPos + 1;
                }

                queue.Add(deliveryQ);
            }
            return queue;
        }

        private void SendObject(DeliveryQueue p)
        {
            searchRes.Clear();
            searchString = p.objectUUID;
            if (searchRes.Count <= 0)
            {
                Instance.MainForm.TabConsole.DisplayNotificationInChat(pluginName + ": Product not found '" + searchString + "' for user '"+p.userUUID+"'", ChatBufferTextStyle.Error);
                return;
            }
            if (searchRes.Count > 1) {
                Instance.MainForm.TabConsole.DisplayNotificationInChat(pluginName + ": More then one product found for '" + searchString + "'", ChatBufferTextStyle.Error);
                return;
            }
            
            var inv = searchRes[0] as InventoryItem;
            if(inv == null) {
                Instance.MainForm.TabConsole.DisplayNotificationInChat(pluginName + ": Product found, but not an inventory item", ChatBufferTextStyle.Error);
                return;
            }

            Manager.GiveItem(inv.UUID, inv.Name, inv.AssetType, OpenMetaverse.UUID.Parse(p.userUUID), false);
            Instance.MainForm.TabConsole.DisplayNotificationInChat(pluginName + ": PRODUCT '" + searchRes[0].Name + "' SENT TO " + p.userUUID, ChatBufferTextStyle.StatusBlue);

            Dictionary<string,string> param = new Dictionary<string,string>();
            param.Add("id", p.id);
            this.RequestVendor("SETDELIVERED", param);
        }

        private bool isSending = false;
        private void productCallback(object obj)
        {
            Manager = Client.Inventory;
            Inventory = Manager.Store;
            Inventory.RootFolder.OwnerID = Client.Self.AgentID;

            if (isSending == true)
            {
                Instance.MainForm.TabConsole.DisplayNotificationInChat(pluginName + ": Waiting...");
                return;
            }
            isSending = true;

            Instance.MainForm.TabConsole.DisplayNotificationInChat(pluginName + ": Queue List");

            var strContent = this.RequestVendor("GETOUTSTANDING");
            List<DeliveryQueue> queue = this.parseResponse(strContent);

            // check if i have something to do
            if (queue.Count <= 0) return;

            foreach (DeliveryQueue p in queue)
                this.SendObject(p);

            /*var grouped = queue.GroupBy(p => p.objectUUID).Select(t=> new { count = t.Count(), UUID = t.Key });
            foreach (var g in grouped)
            {
                var userIds = queue.Where(p => p.objectUUID == g.UUID).Select(p => p.id);
                if (userIds.Count() > 0)
                {
                    var users = String.Join(",", userIds.ToArray());
                    Instance.MainForm.TabConsole.DisplayNotificationInChat(pluginName + ":" + users, ChatBufferTextStyle.Normal);
                }
            }*/

            isSending = false;
        }
    }
}
