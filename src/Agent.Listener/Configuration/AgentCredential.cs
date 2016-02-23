using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Microsoft.VisualStudio.Services.Agent.Configuration
{
    public class CredentialData
    {
        public string Scheme { get; set; }
        public Dictionary<string, string> Data { get; set; }
    }

    public interface ICredentialProvider
    {
        CredentialData CredentialData { get; set; }
        // TODO: (bryanmac) abstract GetVSSCredential which knows how to instantiate based off data
        void ReadCredential(IHostContext context, Dictionary<string, string> args, bool enforceSupplied);
    }

    public abstract class CredentialProvider : ICredentialProvider
    {
        public CredentialProvider(string scheme)
        {
            CredentialData = new CredentialData();
            CredentialData.Scheme = scheme;
            CredentialData.Data = new Dictionary<string, string>();
        }

        public CredentialData CredentialData { get; set; }

        // TODO: (bryanmac) abstract GetVSSCredential which knows how to instantiate based off data

        public abstract void ReadCredential(IHostContext context, Dictionary<string, string> args, bool enforceSupplied);
    }

    public sealed class PersonalAccessToken : CredentialProvider
    {
        public PersonalAccessToken(): base("PAT") {}
        
        public override void ReadCredential(IHostContext context, Dictionary<string, string> args, bool enforceSupplied)
        {
            TraceSource trace = context.GetTrace("PersonalAccessToken");
            trace.Info("ReadCredentials()");

            var wizard = context.GetService<IConsoleWizard>();
            trace.Verbose("reading token");
            string tokenVal = wizard.ReadValue("token", 
                                            "PersonalAccessToken", 
                                            true, 
                                            String.Empty, 
                                            // can do better
                                            Validators.NonEmptyValidator,
                                            args, 
                                            enforceSupplied);
            CredentialData.Data["token"] = tokenVal;
        }        
    }

    public sealed class AlternateCredential : CredentialProvider
    {
        public AlternateCredential(): base("ALT") {}
        
        public override void ReadCredential(IHostContext context, Dictionary<string, string> args, bool enforceSupplied)
        {
            var wizard = context.GetService<IConsoleWizard>();
            CredentialData.Data["Username"] = wizard.ReadValue("username", 
                                            "Username", 
                                            false,
                                            String.Empty,
                                            // can do better
                                            Validators.NonEmptyValidator, 
                                            args, 
                                            enforceSupplied);

            CredentialData.Data["Password"] = wizard.ReadValue("password", 
                                            "Password", 
                                            true,
                                            String.Empty,
                                            // can do better
                                            Validators.NonEmptyValidator,
                                            args, 
                                            enforceSupplied);            
        }        
    }    
}