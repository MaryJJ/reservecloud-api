﻿using System;
using System.ComponentModel.DataAnnotations;

namespace APICore.Common.DTO.Request
{
    public class SignUpRequest
    {
        [Required]
        public string FullName { get; set; }

        [Required]
        [MinLength(6)]
        public string Password { get; set; }

        //[Compare("Password", ErrorMessage = "The password and confirmation password do not match.")]
        //public string ConfirmationPassword { get; set; }

        [Required]
        [EmailAddress]
        public string Email { get; set; }

        public DateTime Birthday { get; set; }

        public string Phone { get; set; }

        public int Gender { get; set; }
        public bool Social { get; set; }
    }
}