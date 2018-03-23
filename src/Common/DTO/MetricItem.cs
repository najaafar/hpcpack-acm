﻿namespace Microsoft.HpcAcm.Common.Dto
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    public class MetricItem
    {
        public string Category { get; set; }

        public Dictionary<string, double?> InstanceValues { get; set; }
    }
}
