/*++ BUILD Version: 0002    // Increment this if a change has global effects

Copyright (c) 1991-1999  Microsoft Corporation

Module Name:

    lmsname.h

Abstract:

    This file contains service name strings.  It is included by lmsvc.h.

Environment:

    User Mode -Win32


--*/

#ifndef _LMSNAME_
#define _LMSNAME_

#if _MSC_VER > 1000
#pragma once
#endif
#include <winapifamily.h>

#pragma region Desktop Family
#if WINAPI_FAMILY_PARTITION(WINAPI_PARTITION_DESKTOP)


//
//  Standard LAN Manager service names.
//

#define SERVICE_WORKSTATION       TEXT("LanmanWorkstation")
#define SERVICE_LM20_WORKSTATION  TEXT("WORKSTATION")
#define WORKSTATION_DISPLAY_NAME  TEXT("Workstation")

#define SERVICE_SERVER            TEXT("LanmanServer")
#define SERVICE_LM20_SERVER       TEXT("SERVER")
#define SERVER_DISPLAY_NAME       TEXT("Server")

#define SERVICE_BROWSER           TEXT("BROWSER")
#define SERVICE_LM20_BROWSER      SERVICE_BROWSER

#define SERVICE_MESSENGER         TEXT("MESSENGER")
#define SERVICE_LM20_MESSENGER    SERVICE_MESSENGER

#define SERVICE_NETRUN            TEXT("NETRUN")
#define SERVICE_LM20_NETRUN       SERVICE_NETRUN

#define SERVICE_SPOOLER           TEXT("SPOOLER")
#define SERVICE_LM20_SPOOLER      SERVICE_SPOOLER

#define SERVICE_ALERTER           TEXT("ALERTER")
#define SERVICE_LM20_ALERTER      SERVICE_ALERTER

#define SERVICE_NETLOGON          TEXT("NETLOGON")
#define SERVICE_LM20_NETLOGON     SERVICE_NETLOGON

#define SERVICE_NETPOPUP          TEXT("NETPOPUP")
#define SERVICE_LM20_NETPOPUP     SERVICE_NETPOPUP

#define SERVICE_SQLSERVER         TEXT("SQLSERVER")
#define SERVICE_LM20_SQLSERVER    SERVICE_SQLSERVER

#define SERVICE_REPL              TEXT("REPLICATOR")
#define SERVICE_LM20_REPL         SERVICE_REPL

#define SERVICE_RIPL              TEXT("REMOTEBOOT")
#define SERVICE_LM20_RIPL         SERVICE_RIPL

#define SERVICE_TIMESOURCE        TEXT("TIMESOURCE")
#define SERVICE_LM20_TIMESOURCE   SERVICE_TIMESOURCE

#define SERVICE_AFP               TEXT("AFP")
#define SERVICE_LM20_AFP          SERVICE_AFP

#define SERVICE_UPS               TEXT("UPS")
#define SERVICE_LM20_UPS          SERVICE_UPS

#define SERVICE_XACTSRV           TEXT("XACTSRV")
#define SERVICE_LM20_XACTSRV      SERVICE_XACTSRV

#define SERVICE_TCPIP             TEXT("TCPIP")
#define SERVICE_LM20_TCPIP        SERVICE_TCPIP

#define SERVICE_NBT               TEXT("NBT")
#define SERVICE_LM20_NBT          SERVICE_NBT

#define SERVICE_LMHOSTS           TEXT("LMHOSTS")
#define SERVICE_LM20_LMHOSTS      SERVICE_LMHOSTS

#define SERVICE_TELNET            TEXT("Telnet")
#define SERVICE_LM20_TELNET       SERVICE_TELNET

#define SERVICE_SCHEDULE          TEXT("Schedule")
#define SERVICE_LM20_SCHEDULE     SERVICE_SCHEDULE

#define SERVICE_NTLMSSP           TEXT("NtLmSsp")

#define SERVICE_DHCP              TEXT("DHCP")
#define SERVICE_LM20_DHCP         SERVICE_DHCP

#define SERVICE_NWSAP             TEXT("NwSapAgent")
#define SERVICE_LM20_NWSAP        SERVICE_NWSAP
#define NWSAP_DISPLAY_NAME        TEXT("NW Sap Agent")

#define SERVICE_NWCS              TEXT("NWCWorkstation")
#define SERVICE_DNS_CACHE         TEXT("DnsCache")

#define SERVICE_W32TIME           TEXT("w32time")
#define SERVCE_LM20_W32TIME       SERVICE_W32TIME

#define SERVICE_KDC               TEXT("kdc")
#define SERVICE_LM20_KDC          SERVICE_KDC

#define SERVICE_LOCALKDC          TEXT("localkdc")
#define SERVICE_LM20_LOCALKDC     SERVICE_LOCALKDC

#define SERVICE_RPCLOCATOR        TEXT("RPCLOCATOR")
#define SERVICE_LM20_RPCLOCATOR   SERVICE_RPCLOCATOR

#define SERVICE_TRKSVR            TEXT("TrkSvr")
#define SERVICE_LM20_TRKSVR       SERVICE_TRKSVR

#define SERVICE_TRKWKS            TEXT("TrkWks")
#define SERVICE_LM20_TRKWKS       SERVICE_TRKWKS

#define SERVICE_NTFRS             TEXT("NtFrs")
#define SERVICE_LM20_NTFRS        SERVICE_NTFRS

#define SERVICE_ISMSERV           TEXT("IsmServ")
#define SERVICE_LM20_ISMSERV      SERVICE_ISMSERV

#define SERVICE_NTDS              TEXT("NTDS")
#define SERVICE_LM20_NTDS         SERVICE_NTDS

#define SERVICE_ADWS              TEXT("ADWS")

#define SERVICE_DSROLE            TEXT("DsRoleSvc")
#define SERVICE_LM20_DSROLE       SERVICE_DSROLE


#endif /* WINAPI_FAMILY_PARTITION(WINAPI_PARTITION_DESKTOP) */
#pragma endregion

#endif
