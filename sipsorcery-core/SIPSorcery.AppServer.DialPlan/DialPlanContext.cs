﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Sys;
using log4net;

namespace SIPSorcery.AppServer.DialPlan
{
    public delegate void CallCancelledDelegate(CallCancelCause cancelCause);
    public delegate void CallProgressDelegate(SIPResponseStatusCodesEnum progressStatus, string reasonPhrase, string[] customHeaders, string progressContentType, string progressBody);
    public delegate void CallFailedDelegate(SIPResponseStatusCodesEnum failureStatus, string reasonPhrase, string[] customHeaders);
    public delegate void CallAnsweredDelegate(SIPResponseStatusCodesEnum answeredStatus, string reasonPhrase, string toTag, string[] customHeaders, string answeredContentType, string answeredBody, SIPDialogue answeredDialogue, SIPDialogueTransferModesEnum uasTransferMode);

    public enum DialPlanAppResult
    {
        Unknown = 0,
        Answered = 1,           // The application answered the call.
        NoAnswer = 2,           // The application had at least one call provide a ringing response.
        Failed = 3,             // The application failed to get any calls to the progressing stage.
        ClientCancelled = 4,    // Call cancelled by client user agent.
        AdminCancelled = 5,     // Call cancelled by a an external administrative action or dial plan rule.
        TimedOut = 6,           // No response from any forward within the time limit.
        Error = 7,
        AlreadyAnswered = 8,    // Was answered prior to the Dial command.
    }

    public enum DialPlanContextsEnum
    {
        None = 0,
        Line = 1,
        Script = 2,
    }

    public enum CallCancelCause
    {
        Unknown = 0,
        TimedOut = 1,           // The call was automatically cancelled by the Dial application after a timeout.
        Administrative = 2,     // Call was cancelled by an administrative action such as clicking cancel on the Call Manager UI.
        ClientCancelled = 3,
        NormalClearing = 4,
        Error = 5,
    }

    public class DialPlanContext {

        private const string TRACE_FROM_ADDRESS = "siptrace@sipsorcery.com";
        private const string TRACE_SUBJECT = "SIP Sorcery Trace";
        
        protected static ILog logger = AppState.GetLogger("dialplan");
        private string CRLF = SIPConstants.CRLF;

        private SIPMonitorLogDelegate Log_External;
        public DialogueBridgeCreatedDelegate CreateBridge_External;
        private DecrementDialPlanExecutionCountDelegate DecrementDialPlanExecutionCount_External;

        private SIPTransport m_sipTransport;
        private ISIPServerUserAgent m_sipServerUserAgent;
        private SIPEndPoint m_outboundProxy;        // If this app forwards calls via an outbound proxy this value will be set.
        private string m_traceDirectory;

        protected List<SIPProvider> m_sipProviders;
        protected StringBuilder m_traceLog = new StringBuilder();
        protected SIPDialPlan m_dialPlan;

        public SIPDialPlan SIPDialPlan
        {
            get { return m_dialPlan; }
        }
        public bool SendTrace = true;                      // True means the trace should be sent, false it shouldn't.
        public DialPlanContextsEnum ContextType;
        public string Owner {
            get { return m_dialPlan.Owner; }
        }
        public string AdminMemberId {
            get { return m_dialPlan.AdminMemberId; }
        }
        public string TraceEmailAddress
        {
            get { return m_dialPlan.TraceEmailAddress; }
        }
        public List<SIPProvider> SIPProviders {
            get { return m_sipProviders; }
        }
        public string DialPlanScript {
            get { return m_dialPlan.DialPlanScript; }
        }
        public StringBuilder TraceLog
        {
            get { return m_traceLog; }
        }

        public string CallersNetworkId;             // If the caller was a locally administered SIP account this will hold it's network id. Used so calls between two accounts on the same local network can be identified.
        public Guid CustomerId;                     // The id of the customer that owns this dialplan.

        private bool m_isAnswered;
        public bool IsAnswered {
            get { return m_isAnswered; }
        }

        public SIPAccount SIPAccount {
            get { return m_sipServerUserAgent.SIPAccount; }
        }

        internal event CallCancelledDelegate CallCancelledByClient;

        public DialPlanContext(
            SIPMonitorLogDelegate monitorLogDelegate,
            SIPTransport sipTransport,
            DialogueBridgeCreatedDelegate createBridge,
            DecrementDialPlanExecutionCountDelegate decrementDialPlanCountDelegate,
            SIPEndPoint outboundProxy,
            ISIPServerUserAgent sipServerUserAgent,
            SIPDialPlan dialPlan,
            List<SIPProvider> sipProviders,
            string traceDirectory,
            string callersNetworkId,
            Guid customerId) {

            Log_External = monitorLogDelegate;
            CreateBridge_External = createBridge;
            DecrementDialPlanExecutionCount_External = decrementDialPlanCountDelegate;
            m_sipTransport = sipTransport;
            m_outboundProxy = outboundProxy;
            m_sipServerUserAgent = sipServerUserAgent;
            m_dialPlan = dialPlan;
            m_sipProviders = sipProviders;
            m_traceDirectory = traceDirectory;
            CallersNetworkId = callersNetworkId;
            CustomerId = customerId;

            m_sipServerUserAgent.CallCancelled += ClientCallCancelled;
            m_sipServerUserAgent.NoRingTimeout += ClientCallNoRingTimeout;
            m_sipServerUserAgent.TransactionComplete += ClientTransactionRemoved;
            m_sipServerUserAgent.SetTraceDelegate(TransactionTraceMessage);
        }

        public void CallProgress(SIPResponseStatusCodesEnum progressStatus, string reasonPhrase, string[] customHeaders, string progressContentType, string progressBody) {
            if (!m_isAnswered) {
                m_sipServerUserAgent.Progress(progressStatus, reasonPhrase, customHeaders, progressContentType, progressBody);
            }
        }

        public void CallFailed(SIPResponseStatusCodesEnum failureStatus, string reasonPhrase, string[] customHeaders) {
            if (!m_isAnswered) {
                m_isAnswered = true;
                m_sipServerUserAgent.Reject(failureStatus, reasonPhrase, customHeaders);
            }
        }

        public void CallAnswered(SIPResponseStatusCodesEnum answeredStatus, string reasonPhrase, string toTag, string[] customHeaders, string answeredContentType, string answeredBody, SIPDialogue answeredDialogue, SIPDialogueTransferModesEnum uasTransferMode) {
            try {
                if (!m_isAnswered) {
                    m_isAnswered = true;
                    Log_External(new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.DialPlan, "Answering client call with a response status of " + (int)answeredStatus + ".", Owner));

                    SIPDialogue uasDialogue = m_sipServerUserAgent.Answer(answeredContentType, answeredBody, toTag, answeredDialogue, uasTransferMode);

                    if (!m_sipServerUserAgent.IsB2B && answeredDialogue != null) {
                        if (uasDialogue != null) {
                            // Record the now established call with the call manager for in dialogue management and hangups.
                            CreateBridge_External(uasDialogue, answeredDialogue, m_dialPlan.Owner);
                        }
                        else {
                            logger.Warn("Failed to get a SIPDialogue from UAS.Answer.");
                        }
                    }
                }
                else {
                    logger.Warn("DialPlanContext CallAnswered fired on already answered call.");
                }
            }
            catch (Exception excp) {
                logger.Error("Exception DialPlanContext CallAnswered. " + excp.Message);
            }
        }

        public void DecrementDialPlanExecutionCount()
        {
            DecrementDialPlanExecutionCount_External(m_dialPlan, CustomerId);
        }

        /// <summary>
        /// The client transaction will time out after ringing for the maximum allowed time for an INVITE transaction (probably 10 minutes) or less
        /// if the invite transaction timeout value has been adjusted.
        /// </summary>
        /// <param name="sipTransaction"></param>
        private void ClientCallNoRingTimeout(ISIPServerUserAgent sipServerUserAgent) {
            try {
                m_isAnswered = true;
                Log_External(new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.DialPlan, "Client call timed out, no ringing response was receved within the allowed time.", Owner));
                if (CallCancelledByClient != null) {
                    CallCancelledByClient(CallCancelCause.TimedOut);
                }
            }
            catch (Exception excp) {
                logger.Error("Exception ClientCallNoRingTimeout. " + excp.Message);
            }
        }

        private void ClientCallCancelled(ISIPServerUserAgent uas) {
            try {
                if (!m_isAnswered) {
                    m_isAnswered = true;
                    Log_External(new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.DialPlan, "Client call cancelled halting dial plan.", Owner));
                    if (CallCancelledByClient != null) {
                        CallCancelledByClient(CallCancelCause.ClientCancelled);
                    }
                }
                else {
                    logger.Warn("DialPlanContext ClientCallCancelled fired on already answered call.");
                }
            }
            catch (Exception excp) {
                logger.Error("Exception DialPlanContext ClientCallCancelled. " + excp.Message);
            }
        }

        private void ClientTransactionRemoved(ISIPServerUserAgent uas) {
            try {
                if (!TraceEmailAddress.IsNullOrBlank() && TraceLog != null && TraceLog.Length > 0 && SendTrace)
                {
                    ThreadPool.QueueUserWorkItem(delegate { CompleteTrace(); });
                }
            }
            catch (Exception excp) {
                logger.Error("Exception DialPlanContext ClientTransactionRemoved. " + excp.Message);
            }
        }

        private void CompleteTrace() {
            try {
                SIPMonitorEvent traceCompleteEvent = new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.DialPlan, "Dialplan trace completed at " + DateTime.Now.ToString("dd MMM yyyy HH:mm:ss:fff") + ".", Owner);
                TraceLog.AppendLine(traceCompleteEvent.EventType + "=> " + traceCompleteEvent.Message);

                if (!m_traceDirectory.IsNullOrBlank() && Directory.Exists(m_traceDirectory))
                {
                    string traceFilename = m_traceDirectory + Owner + "-" + DateTime.Now.ToString("ddMMMyyyyHHmmss") + ".txt";
                    StreamWriter traceSW = new StreamWriter(traceFilename);
                    traceSW.Write(TraceLog.ToString());
                    traceSW.Close();
                }

                if (TraceEmailAddress != null) {
                    logger.Debug("Emailing trace to " + TraceEmailAddress + ".");
                    Email.SendEmail(TraceEmailAddress, TRACE_FROM_ADDRESS, TRACE_SUBJECT, TraceLog.ToString());
                }
            }
            catch (Exception traceExcp) {
                logger.Error("Exception DialPlanContext CompleteTrace. " + traceExcp.Message);
            }
        }

        private void TransactionTraceMessage(SIPTransaction sipTransaction, string message) {
            FireProxyLogEvent(new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.SIPTransaction, message, Owner));
        }

        private void FireProxyLogEvent(SIPMonitorEvent monitorEvent) {
            try {
                if (TraceLog != null) {
                    TraceLog.AppendLine(monitorEvent.EventType + "=> " + monitorEvent.Message);
                }

                if (Log_External != null) {
                    Log_External(monitorEvent);
                }
            }
            catch (Exception excp) {
                logger.Error("Exception FireProxyLogEvent DialPlanContext. " + excp.Message);
            }
        }
    }
}