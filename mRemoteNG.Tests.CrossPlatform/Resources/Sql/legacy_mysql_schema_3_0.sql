-- Verbatim copy of the MySQL schema script of the legacy WinForms app
-- (mRemoteNG/Config/Serializers/ConnectionSerializers/Sql/SqlDatabaseMetaDataRetriever.cs, schema 3.0).
-- Used by the integration tests to create a database exactly as the legacy app does.
/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET @OLD_CHARACTER_SET_RESULTS=@@CHARACTER_SET_RESULTS */;
/*!40101 SET @OLD_COLLATION_CONNECTION=@@COLLATION_CONNECTION */;
/*!40101 SET NAMES utf8 */;
/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;
/*!40103 SET TIME_ZONE='+00:00' */;
/*!40014 SET @OLD_UNIQUE_CHECKS=@@UNIQUE_CHECKS, UNIQUE_CHECKS=0 */;
/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;

--
-- Table structure for table `tblCons`
--

DROP TABLE IF EXISTS `tblCons`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8 */;
CREATE TABLE `tblCons` (
    `ID` int NOT NULL AUTO_INCREMENT,
    `ConstantID` varchar(128) NOT NULL,
    `PositionID` int NOT NULL,
    `ParentID` varchar(128) DEFAULT NULL,
    `LastChange` datetime NOT NULL,
    `Name` varchar(128) NOT NULL,
    `Type` varchar(32) NOT NULL,
    `Expanded` tinyint NOT NULL,
    `AutomaticResize` tinyint NOT NULL DEFAULT 1,
    `CacheBitmaps` tinyint NOT NULL,
    `Colors` varchar(32) NOT NULL,
    `ConnectToConsole` tinyint NOT NULL,
    `Connected` tinyint NOT NULL,
    `Description` varchar(1024) DEFAULT NULL,
    `DisableCursorBlinking` tinyint NOT NULL,
    `DisableCursorShadow` tinyint NOT NULL,
    `DisableFullWindowDrag` tinyint NOT NULL,
    `DisableMenuAnimations` tinyint NOT NULL,
    `DisplayThemes` tinyint NOT NULL,
    `DisplayWallpaper` tinyint NOT NULL,
    `Domain` varchar(512) DEFAULT NULL,
    `EnableDesktopComposition` tinyint NOT NULL,
    `EnableFontSmoothing` tinyint NOT NULL,
    `ExtApp` varchar(256) DEFAULT NULL,
    `Favorite` tinyint NOT NULL,
    `Hostname` varchar(512) DEFAULT NULL,
    `Icon` varchar(128) NOT NULL,
    `LoadBalanceInfo` varchar(1024) DEFAULT NULL,
    `MacAddress` varchar(32) DEFAULT NULL,
    `OpeningCommand` varchar(512) DEFAULT NULL,
    `Panel` varchar(128) NOT NULL,
    `Password` varchar(1024) DEFAULT NULL,
    `Port` int NOT NULL,
    `PostExtApp` varchar(256) DEFAULT NULL,
    `PreExtApp` varchar(256) DEFAULT NULL,
    `Protocol` varchar(32) NOT NULL,
    `PuttySession` varchar(128) DEFAULT NULL,
    `RDGatewayDomain` varchar(512) DEFAULT NULL,
    `RDGatewayHostname` varchar(512) DEFAULT NULL,
    `RDGatewayPassword` varchar(1024) DEFAULT NULL,
    `RDGatewayUsageMethod` varchar(32) NOT NULL,
    `RDGatewayUseConnectionCredentials` varchar(32) NOT NULL,
    `RDGatewayUsername` varchar(512) DEFAULT NULL,
    `RDPAlertIdleTimeout` tinyint NOT NULL,
    `RDPAuthenticationLevel` varchar(32) NOT NULL,
    `RDPMinutesToIdleTimeout` int(11) NOT NULL,
    `RdpVersion` varchar(10) DEFAULT NULL,
    `RedirectAudioCapture` tinyint NOT NULL,
    `RedirectClipboard` tinyint NOT NULL,
    `RedirectDiskDrives` varchar(32) DEFAULT NULL,
    `RedirectDiskDrivesCustom` varchar(32) DEFAULT NULL,
    `RedirectKeys` tinyint NOT NULL,
    `RedirectPorts` tinyint NOT NULL,
    `RedirectPrinters` tinyint NOT NULL,
    `RedirectSmartCards` tinyint NOT NULL,
    `RedirectSound` varchar(64) NOT NULL,
    `RenderingEngine` varchar(32) DEFAULT NULL,
    `Resolution` varchar(32) NOT NULL,
    `SSHOptions` varchar(1024) NOT NULL,
    `SSHTunnelConnectionName` varchar(128) NOT NULL,
    `SoundQuality` varchar(20) NOT NULL,
    `UseCredSsp` tinyint NOT NULL,
    `UseEnhancedMode` tinyint NOT NULL,
    `UseVmId` tinyint NOT NULL,
    `UserField` varchar(256) DEFAULT NULL,
    `Username` varchar(512) DEFAULT NULL,
    `VNCAuthMode` varchar(10) DEFAULT NULL,
    `VNCColors` varchar(10) DEFAULT NULL,
    `VNCCompression` varchar(10) DEFAULT NULL,
    `VNCEncoding` varchar(20) DEFAULT NULL,
    `VNCProxyIP` varchar(128) DEFAULT NULL,
    `VNCProxyPassword` varchar(1024) DEFAULT NULL,
    `VNCProxyPort` int DEFAULT NULL,
    `VNCProxyType` varchar(20) DEFAULT NULL,
    `VNCProxyUsername` varchar(512) DEFAULT NULL,
    `VNCSmartSizeMode` varchar(20) DEFAULT NULL,
    `VNCViewOnly` tinyint NOT NULL,
    `VmId` varchar(512) DEFAULT NULL,
    `ICAEncryptionStrength` varchar(32) NOT NULL,
    `InheritAutomaticResize` tinyint NOT NULL,
    `InheritCacheBitmaps` tinyint NOT NULL,
    `InheritColors` tinyint NOT NULL,
    `InheritDescription` tinyint NOT NULL,
    `InheritDisableCursorBlinking` tinyint NOT NULL,
    `InheritDisableCursorShadow` tinyint NOT NULL,
    `InheritDisableFullWindowDrag` tinyint NOT NULL,
    `InheritDisableMenuAnimations` tinyint NOT NULL,
    `InheritDisplayThemes` tinyint NOT NULL,
    `InheritDisplayWallpaper` tinyint NOT NULL,
    `InheritDomain` tinyint NOT NULL,
    `InheritEnableDesktopComposition` tinyint NOT NULL,
    `InheritEnableFontSmoothing` tinyint NOT NULL,
    `InheritExtApp` tinyint NOT NULL,
    `InheritFavorite` tinyint NOT NULL,
    `InheritICAEncryptionStrength` tinyint NOT NULL,
    `InheritIcon` tinyint NOT NULL,
    `InheritLoadBalanceInfo` tinyint NOT NULL,
    `InheritMacAddress` tinyint NOT NULL,
    `InheritOpeningCommand` tinyint NOT NULL,
    `InheritPanel` tinyint NOT NULL,
    `InheritPassword` tinyint NOT NULL,
    `InheritPort` tinyint NOT NULL,
    `InheritPostExtApp` tinyint NOT NULL,
    `InheritPreExtApp` tinyint NOT NULL,
    `InheritProtocol` tinyint NOT NULL,
    `InheritPuttySession` tinyint NOT NULL,
    `InheritRDGatewayDomain` tinyint NOT NULL,
    `InheritRDGatewayHostname` tinyint NOT NULL,
    `InheritRDGatewayPassword` tinyint NOT NULL,
    `InheritRDGatewayUsageMethod` tinyint NOT NULL,
    `InheritRDGatewayUseConnectionCredentials` tinyint NOT NULL,
    `InheritRDGatewayExternalCredentialProvider` tinyint NOT NULL,
    `InheritRDGatewayUsername` tinyint NOT NULL,
    `InheritRDGatewayUserViaAPI` tinyint NOT NULL,
    `InheritRDPAlertIdleTimeout` tinyint NOT NULL,
    `InheritRDPAuthenticationLevel` tinyint NOT NULL,
    `InheritRDPMinutesToIdleTimeout` tinyint NOT NULL,
    `InheritRdpVersion` tinyint NOT NULL,
    `InheritRedirectAudioCapture` tinyint NOT NULL,
    `InheritRedirectClipboard` tinyint NOT NULL,
    `InheritRedirectDiskDrives` tinyint NOT NULL,
    `InheritRedirectDiskDrivesCustom` tinyint NOT NULL,
    `InheritRedirectKeys` tinyint NOT NULL,
    `InheritRedirectPorts` tinyint NOT NULL,
    `InheritRedirectPrinters` tinyint NOT NULL,
    `InheritRedirectSmartCards` tinyint NOT NULL,
    `InheritRedirectSound` tinyint NOT NULL,
    `InheritRenderingEngine` tinyint NOT NULL,
    `InheritResolution` tinyint NOT NULL,
    `InheritSSHOptions` tinyint NOT NULL,
    `InheritSSHTunnelConnectionName` tinyint NOT NULL,
    `InheritSoundQuality` tinyint NOT NULL,
    `InheritUseConsoleSession` tinyint NOT NULL,
    `InheritUseCredSsp` tinyint NOT NULL,
    `InheritUseRestrictedAdmin` tinyint NOT NULL,
    `InheritUseRCG` tinyint NOT NULL,
    `InheritExternalCredentialProvider` tinyint NOT NULL,
    `InheritUserViaAPI` tinyint NOT NULL,
    `UseRestrictedAdmin` tinyint NOT NULL,
    `UseRCG` tinyint NOT NULL,
    `InheritUseEnhancedMode` tinyint DEFAULT NULL,
    `InheritUseVmId` tinyint DEFAULT NULL,
    `InheritUserField` tinyint NOT NULL,
    `InheritUsername` tinyint NOT NULL,
    `InheritVNCAuthMode` tinyint NOT NULL,
    `InheritVNCColors` tinyint NOT NULL,
    `InheritVNCCompression` tinyint NOT NULL,
    `InheritVNCEncoding` tinyint NOT NULL,
    `InheritVNCProxyIP` tinyint NOT NULL,
    `InheritVNCProxyPassword` tinyint NOT NULL,
    `InheritVNCProxyPort` tinyint NOT NULL,
    `InheritVNCProxyType` tinyint NOT NULL,
    `InheritVNCProxyUsername` tinyint NOT NULL,
    `InheritVNCSmartSizeMode` tinyint NOT NULL,
    `InheritVNCViewOnly` tinyint NOT NULL,
    `InheritVmId` tinyint NOT NULL,
    `StartProgram` varchar(512) DEFAULT NULL,
    `StartProgramWorkDir` varchar(512) DEFAULT NULL,
    `EC2Region` varchar(32) DEFAULT NULL,
    `EC2InstanceId` varchar(32) DEFAULT NULL,
    `ExternalCredentialProvider` varchar(256) DEFAULT NULL,
    `ExternalAddressProvider` varchar(256) DEFAULT NULL,
    `UserViaAPI` varchar(512) NOT NULL,
    PRIMARY KEY (`ConstantID`),
    UNIQUE KEY `ID_UNIQUE` (`ID`),
    UNIQUE KEY `ConstantID_UNIQUE` (`ConstantID`)
) ENGINE=InnoDB AUTO_INCREMENT=3324 DEFAULT CHARSET=latin1;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `tblRoot`
--

DROP TABLE IF EXISTS `tblRoot`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8 */;
CREATE TABLE `tblRoot` (
    `Name` varchar(2048) NOT NULL,
    `Export` tinyint NOT NULL,
    `Protected` varchar(4048) NOT NULL,
    `ConfVersion` varchar(15) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=latin1;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `tblUpdate`
--

DROP TABLE IF EXISTS `tblUpdate`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8 */;
CREATE TABLE `tblUpdate` (
    `LastUpdate` datetime(3) DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=latin1;
/*!40101 SET character_set_client = @saved_cs_client */;


/*!40103 SET TIME_ZONE=@OLD_TIME_ZONE */;

/*!40101 SET SQL_MODE=@OLD_SQL_MODE */;
/*!40014 SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS */;
/*!40014 SET UNIQUE_CHECKS=@OLD_UNIQUE_CHECKS */;
/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40101 SET CHARACTER_SET_RESULTS=@OLD_CHARACTER_SET_RESULTS */;
/*!40101 SET COLLATION_CONNECTION=@OLD_COLLATION_CONNECTION */;
/*!40111 SET SQL_NOTES=@OLD_SQL_NOTES */;
