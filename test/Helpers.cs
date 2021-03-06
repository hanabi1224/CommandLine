﻿using Xunit;
using System;
using System.Collections.Generic;
using OutputColorizer;
using System.IO;
using System.Reflection;

namespace CommandLine.Tests
{
    class Helpers
    {
        public static string GetTestLocation()
        {
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        public static T Parse<T>(string argString, IOutputWriter writer = null) where T : new()
        {
            if (writer == null)
                writer = new ConsoleWriter();

            Colorizer.SetupWriter(writer);
            return Parser.Parse<T>(argString);
        }

        public static void DisplayHelp<T>(HelpFormat helpFormat, IOutputWriter writer = null) where T : new()
        {
            if (writer == null)
                writer = new ConsoleWriter();

            Colorizer.SetupWriter(writer);
            Parser.DisplayHelp<T>(helpFormat);
        }

        public static void CollectionEquals(List<string> actual, params string[] expected)
        {
            Assert.Equal(actual.Count, expected.Length);

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(actual[i], expected[i]);
            }
        }
    }
}
