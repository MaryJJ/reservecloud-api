using System;
using System.Collections.Generic;
using System.Text;

namespace APICore.Common.DTO.Request
{
    public class LoginSocialRequest
    {
        public string Uid { get; set; }
        public string Token { get; set; }
        public string Provider { get; set; }
    }
}
