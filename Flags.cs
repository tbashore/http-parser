using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HttpParser
{
    internal enum Flags
    {
        F_CHUNKED = 1 << 0,
        F_CONNECTION_KEEP_ALIVE = 1 << 1,
        F_CONNECTION_CLOSE = 1 << 2,
        F_TRAILING = 1 << 3,
        F_UPGRADE = 1 << 4,
        F_SKIPBODY = 1 << 5
    }
}
