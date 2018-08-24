program uModPluginUpdater;

{$APPTYPE CONSOLE}
{$R *.res}

uses
  System.SysUtils,
  System.Classes,
  System.StrUtils,
  System.IOUtils,
  System.Types,
  uConsts in 'uConsts.pas',
  IdIOHandler,
  IdIOHandlerSocket,
  IdIOHandlerStack,
  IdCookieManager,
  IdSSL,
  IdSSLOpenSSL,
  IdBaseComponent,
  IdComponent,
  IdTCPConnection,
  IdTCPClient,
  IdHTTP,
  djson in 'djson.pas',
  Math;

type
  TPluginInfo = record
    Name: string;
    Author: string;
    Version: string;
    filename: string;
    Debug: string;
  end;

var
  arrPlugins: array of string;
  arrPluginVersions: array of Integer;
  dynPlugins: TStringDynArray;
  arrNotUpdated: array of string;

function ExtractFileNameEX(const AFileName: string): string;
var
  I: integer;
begin
  I := LastDelimiter('.' + PathDelim + DriveDelim, AFileName);
  if (I = 0) or (AFileName[I] <> '.') then
    I := MaxInt;
  Result := ExtractFileName(Copy(AFileName, 1, I - 1));
end;

function FindTextBetweenTags(const aText, aTagLeft, aTagRight: string): string;
var
  sdata: TStringList;
  iLeft, iRight: Integer;
begin
  sdata := TStringList.Create;
  try
    sdata.Text := aText;

    iLeft := Pos(aTagLeft, sdata.Text) + Length(aTagLeft);
    iRight := Pos(aTagRight, sdata.Text);

    Result := Copy(sdata.Text, iLeft, iRight - iLeft);
  finally
    FreeAndNil(sdata);
  end;
end;

function GetPluginInfo(const aFilename: string): TPluginInfo;
var
  sdata, sinfo: TStringList;
  I: Integer;
  sline: string;
begin
  Result.filename := aFilename;

  if ExtractFileExt(aFilename) = '.cs' then
  begin
    sdata := TStringList.Create;
    try
      sdata.LoadFromFile(aFilename);

      for I := 0 to sdata.Count - 1 do
      begin
        if AnsiContainsStr(sdata[I], 'Info') then
        begin
          sline := sdata[I];
          Break;
        end;
      end;

      sline := FindTextBetweenTags(sline, '(', ')');

      sinfo := TStringList.Create;
      try
        sinfo.Delimiter := ',';
        sinfo.QuoteChar := '"';
        sinfo.DelimitedText := sline;

        Result.Debug := sline;
        Result.Name := sinfo[0];
        Result.Author := sinfo[1];
        Result.Version := sinfo[2];
      finally
        sinfo.Free;
      end;
    finally
      sdata.Free;
    end;
  end;
end;

function CanConnect(const aHost: string; const aPort: Integer): Boolean;
var
  tcp: TIdTCPClient;
begin
  tcp := TIdTCPClient.Create(nil);
  try
    tcp.Host := aHost;
    tcp.Port := aPort;
    tcp.ConnectTimeout := 100;
    tcp.Connect;
    Result := True;
    tcp.Disconnect;
    tcp.Free;
  except
    on E: Exception do
    begin
      Result := False;
      Writeln(E.Message);
      Writeln('');
    end;
  end;
end;

procedure CheckForUpdates;
var
  http: TIdHTTP;
  ssl: TIdSSLIOHandlerSocketOpenSSL;
  Stream: TMemoryStream;
  I, iplugins, iLatestVersion, iUpdated: Integer;
  jdata, jplugin: TdJSON;
  pluginInfo: TPluginInfo;
  bUpdated: Boolean;
begin
  iplugins := 0;
  iUpdated := 0;
  Writeln('Loading Plugins...');
  Writeln('');
  dynPlugins := TDirectory.GetFiles(APP_DIR, '*.cs');

  if Length(dynPlugins) <= 0 then
  begin
    Writeln('No plugins could be found.');
    Writeln('');
    Writeln('Press any key to exit.');
    Readln;
    Exit;
  end;

  SetLength(arrPlugins, Length(dynPlugins));
  SetLength(arrPluginVersions, Length(dynPlugins));
  SetLength(arrNotUpdated, Length(dynPlugins));

  for I := 0 to Length(dynPlugins) - 1 do
  begin
    pluginInfo := GetPluginInfo(dynPlugins[I]);

    arrPlugins[I] := pluginInfo.Name;
    arrPluginVersions[I] := StrToInt(AnsiReplaceStr(pluginInfo.Version, '.', ''));

    Writeln('Found Plugin: ' + pluginInfo.Name);
    Sleep(100);
  end;
  Writeln('');
  Writeln('Total Plugins: ' + IntToStr(Length(arrPlugins)));
  Writeln('');

  Writeln('Checking for updates...');

  http := TIdHTTP.Create(nil);
  try
    ssl := TIdSSLIOHandlerSocketOpenSSL.Create(nil);
    try
      ssl.SSLOptions.SSLVersions := [sslvTLSv1, sslvTLSv1_1, sslvTLSv1_2];
      http.IOHandler := ssl;
      http.HandleRedirects := True;
      http.Request.UserAgent := 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:61.0) Gecko/20100101 Firefox/61.0';

      for I := 0 to Length(arrPlugins) - 1 do
      begin
        bUpdated := False;
        jdata := TdJSON.Parse(http.Get(API_URL + ExtractFileNameEX(dynPlugins[I])));
        try
          for jplugin in jdata['data'] do
          begin
            if jplugin['name'].AsString = ExtractFileNameEX(dynPlugins[I]) then
            begin
              Stream := TMemoryStream.Create;
              try
                Writeln('Found Plugin: ' + arrPlugins[I] + ' ' + jplugin['latest_release_version_formatted'].AsString + ' on uMod.org');
                Inc(iplugins);
                Writeln('Checking ' + arrPlugins[I] + ' for updates...');

                iLatestVersion := StrToInt(AnsiReplaceStr(jplugin['latest_release_version'].AsString, '.', ''));

                if iLatestVersion > arrPluginVersions[I] then
                begin
                  Writeln(arrPlugins[I] + ' is outdated! Updating...');
                  http.Get(jplugin['download_url'].AsString, Stream);
                  Stream.SaveToFile(dynPlugins[I]);
                  Inc(iUpdated);
                end
                else
                  Writeln(arrPlugins[I] + ' is up to date!');

                Writeln('');

                bUpdated := True;
              finally
                Stream.Free;
              end;
            end;
          end;
        finally
          jdata.Free;
        end;

        if not bUpdated then
          arrNotUpdated[I] := dynPlugins[I];
      end;

    finally
      ssl.Free;
    end;
  finally
    http.Free;
  end;

  Writeln('Total plugins found on uMod.org: ' + IntToStr(iplugins) + ' of ' + IntToStr(Length(dynPlugins)) + ' installed.');
  Writeln('Total plugins updated: ' + IntToStr(iUpdated));
  Writeln('');
  Writeln('=================================================');
  Writeln('The following plugins couldn''t be updated as they were not found on uMod.org. They might get added later so keep trying everyday :)');
  Writeln('');
  for I := 0 to Length(arrNotUpdated) - 1 do
  begin
    if arrNotUpdated[I] <> '' then
      Writeln(arrNotUpdated[I]);
  end;
  Writeln('=================================================');
  Writeln('');
  Writeln('Press any key to exit.');
  Readln;
end;

procedure Login(const aUsername, aPassword: string);
var
  http: TIdHTTP;
  ssl: TIdSSLIOHandlerSocketOpenSSL;
  params: TStringList;
begin
  http := TIdHTTP.Create(nil);
  try
    ssl := TIdSSLIOHandlerSocketOpenSSL.Create(nil);
    try
      ssl.SSLOptions.SSLVersions := [sslvTLSv1, sslvTLSv1_1, sslvTLSv1_2];
      http.IOHandler := ssl;
      http.HandleRedirects := True;
      http.Request.UserAgent := 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:61.0) Gecko/20100101 Firefox/61.0';
      http.AllowCookies := True;

      http.Get('https://umod.org/login');

      params := TStringList.Create;
      try
        params.Add('email=' + aUsername);
        params.Add('password=' + aPassword);
        params.Add('remember=1');
        params.Add('redirect=https://umod.org/');

        http.Post('https://umod.org/login', params);
      finally
        params.Free;
      end;
    finally
      ssl.Free;
    end;
  finally
    http.Free;
  end;
end;

begin
  try
    Writeln('Checking connection to uMod.org');
    Writeln('');
    if CanConnect('umod.org', 80) then
    begin
      Sleep(1000);
      Writeln('Connection OK!');
      Writeln('');
      Sleep(1000);
      CheckForUpdates;
     //Login('sd', 'sdf');
    // Readln;
    end
    else
    begin
      Sleep(1000);
      Writeln('Cannot check for updates. uMod.org may be down or you don''t have an active internet connection.');
      Writeln('');
      Writeln('Press any key to close.');
      Readln;
    end;
  except
    on E: Exception do
    begin
      Writeln('==================================================================');
      Writeln('ERROR:');
      Writeln(E.ClassName, ': ', E.Message);
      Writeln('==================================================================');
      Writeln('');
      Writeln('Please report the error above with a screenshot and detailed description on quantumsoftware.co.za');
      Writeln('');
      Writeln('Press any key to exit.');
      Readln;
    end;
  end;

end.

