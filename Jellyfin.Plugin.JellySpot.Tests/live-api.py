"""Optional integration checks against the isolated server prepared under artifacts/jellyfin-live."""
from pathlib import Path
import datetime,json,secrets,sqlite3,time,urllib.request,urllib.error,uuid
root=Path(__file__).resolve().parent.parent/'artifacts/jellyfin-live'
session=json.loads((root/'session.json').read_text(encoding='utf-8'))
base='http://127.0.0.1:18096'
def call(path,method='GET',data=None,token=None):
    headers={'Content-Type':'application/json','X-Emby-Token':token or session['token'],'X-Emby-Authorization':'MediaBrowser Client="JellySpotLiveTests", Device="Browser", DeviceId="live-api", Version="1"'}
    request=urllib.request.Request(base+path,data=None if data is None else json.dumps(data).encode(),method=method,headers=headers)
    with urllib.request.urlopen(request,timeout=10) as response:
        body=response.read();return response.status,json.loads(body) if body else None
public=call('/System/Info/Public')[1]
assert public['Id']==session['serverId'],'Not the isolated test server'
assert call('/Users/'+session['userId'])[1]['Name']=='jellyspot-test'
database=Path(session['db']).resolve();assert database.is_relative_to(root.resolve()),'Database must remain inside isolated artifacts'
collection=session['collectionId'];owner=session['userId'].replace('-','');now=datetime.datetime.now(datetime.timezone.utc)
# Supply a local-only test account link. This operation uses persisted collection metadata and makes no Spotify calls.
with sqlite3.connect(database) as connection:
    connection.execute('INSERT OR REPLACE INTO user_tokens(jellyfin_user_id,json) VALUES(?,?)',(owner,json.dumps({'Generation':'isolated-test','AccessToken':'synthetic-not-a-spotify-token','RefreshToken':'','SpotifyUserId':'synthetic','DisplayName':'Synthetic','Scope':'','ExpiresAtUtc':(now+datetime.timedelta(hours=1)).isoformat()})))
watch=time.perf_counter();status,accepted=call('/JellySpot/Operations','POST',{'Kind':'tracks','CollectionId':collection,'TrackIds':['synthetic-track'],'IdempotencyKey':'live-'+uuid.uuid4().hex});elapsed=(time.perf_counter()-watch)*1000
assert status==202
for _ in range(60):
    operation=call('/JellySpot/Operations/'+accepted['Id'])[1]
    if operation['Status'] not in ('Queued','Running'):break
    time.sleep(.2)
assert operation['Status']=='Completed',operation
assert operation['Processed']==1 and operation['Existing']==1
status,page=call('/JellySpot/Requests?page=1&pageSize=50&sort=requested&direction=desc&q=&filter=')
assert status==200 and page['Items']
print(f'Live operation acknowledgement: {elapsed:.1f} ms; durable progress reached Completed using existing audio.')
# Replay a recovered pending job. Finishing it must trigger a real Jellyfin library scan.
_,tasks_before=call('/ScheduledTasks');scan_before=next(t for t in tasks_before if t.get('Key')=='RefreshLibrary')
with sqlite3.connect(database) as connection:
    connection.execute("UPDATE queue SET status='Pending',json=json_set(json,'$.Status','Pending') WHERE spotify_track_id='synthetic-track'")
for _ in range(80):
    _,tasks=call('/ScheduledTasks');scan=next(t for t in tasks if t.get('Key')=='RefreshLibrary')
    if scan['State']=='Running' or scan.get('LastExecutionResult')!=scan_before.get('LastExecutionResult'):break
    time.sleep(.25)
assert scan['State']=='Running' or scan.get('LastExecutionResult')!=scan_before.get('LastExecutionResult'),'No library scan after completion'
print('Live recovered-job completion triggered Jellyfin library scanning.')
# Verify the actual HTTP authorization boundary with a second user restricted from the music library.
password=secrets.token_urlsafe(24);name='jellyspot-viewer-'+uuid.uuid4().hex[:6]
_,viewer=call('/Users/New','POST',{'Name':name,'Password':password});viewer_id=viewer['Id']
try:
    _,auth=call('/Users/AuthenticateByName','POST',{'Username':name,'Pw':password});viewer_token=auth['AccessToken']
    _,user=call('/Users/'+viewer_id);policy=user['Policy'];policy['EnableAllFolders']=False;policy['EnabledFolders']=[]
    call('/Users/'+viewer_id+'/Policy','POST',policy)
    try:call('/JellySpot/Browser/Collections/'+collection+'/Tracks',token=viewer_token);raise AssertionError('Private collection was exposed')
    except urllib.error.HTTPError as error:assert error.code==404
    call('/JellySpot/Collections/'+collection+'/Sharing','POST',{'Shared':True})
    _,shared=call('/JellySpot/Browser/Collections/'+collection+'/Tracks',token=viewer_token)
    assert len(shared['Items'])==1 and shared['Items'][0].get('ItemId') is None
    call('/JellySpot/Collections/'+collection+'/Follow','POST',{},viewer_token)
    call('/JellySpot/Collections/'+collection+'/Sharing','POST',{'Shared':False})
    try:call('/JellySpot/Browser/Collections/'+collection+'/Tracks',token=viewer_token);raise AssertionError('Unshared collection was exposed')
    except urllib.error.HTTPError as error:assert error.code==404
    _,following=call('/JellySpot/Browser/Collections?view=following',token=viewer_token);assert following['Total']==0
    print('Live private/shared/unshare/follow checks passed; restricted viewers received no playable audio IDs.')
finally:
    call('/JellySpot/Collections/'+collection+'/Sharing','POST',{'Shared':False})
    call('/Users/'+viewer_id,'DELETE')
    call('/JellySpot/OAuth/Unlink','POST',{})
