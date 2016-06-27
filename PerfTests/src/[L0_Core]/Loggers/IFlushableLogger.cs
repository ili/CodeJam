﻿using System;

// ReSharper disable once CheckNamespace

namespace BenchmarkDotNet.Loggers
{
	/// <summary>Extension of <see cref="ILogger"/> interface that supports flush method.</summary>
	public interface IFlushableLogger : ILogger
	{
		/// <summary>Flushes the log.</summary>
		void Flush();
	}
}