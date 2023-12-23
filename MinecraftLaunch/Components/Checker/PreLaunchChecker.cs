﻿using MinecraftLaunch.Classes.Interfaces;
using MinecraftLaunch.Classes.Models.Game;

namespace MinecraftLaunch.Components.Checker {
    public class PreLaunchChecker(GameEntry entry) : IChecker {
        public bool IsCheckResource { get; set; }

        public bool IsCheckAccount { get; set; }

        public ValueTask<bool> CheckAsync() {
            /*
             * 
             */
            throw new NotImplementedException();
        }
    }
}
