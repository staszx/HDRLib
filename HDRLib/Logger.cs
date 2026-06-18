// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib
{
    using Interfaces;

    public class Logger : Disposable
    {
        #region Fields

        private readonly StreamWriter writer;

        #endregion

        #region Constructors

        private Logger(string filePath)
        {
            var stream = File.OpenWrite(filePath);
            this.writer = new StreamWriter(stream);
        }

        #endregion

        #region Properties

        public static Logger Instance { get; set; }

        #endregion

        #region Methods

        public async Task WriteLog(string text)
        {
            var time = DateTime.Now.ToString("dd:MM:yy HH:mm:ss");
            await this.writer.WriteLineAsync($"{time}: {text}");
        }

        public async Task WriteLogWithoutTime(string text)
        {
            await this.writer.WriteLineAsync(text);
        }

        public static Logger CreateLogger(string filePath)
        {
            Instance = new Logger(filePath);
            return Instance;
        }

        protected override void ResourceDispose()
        {
            this.writer.Dispose();
        }

        #endregion
    }
}
