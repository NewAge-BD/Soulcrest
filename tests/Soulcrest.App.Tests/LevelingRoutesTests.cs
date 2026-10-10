using System.Drawing;
using OpenCvSharp;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LevelingRoutesTests : IDisposable
{
    public LevelingRoutesTests() { File.Delete(AppPaths.TargetsFile); File.Delete(AppPaths.RoutesFile); }
    public void Dispose() { File.Delete(AppPaths.TargetsFile); File.Delete(AppPaths.RoutesFile); }

    private static MapTargetsService Service(bool leveling = true, bool repeat = false)
    {
        var stops = Enumerable.Range(1, 8).Select(i=>new RouteStop("altgard",i*100,100,$"Main quest - NPC {i}","NPCs · Hero Quest",null)).ToList();
        stops.Insert(2,new RouteStop("altgard",250,100,"Fly up - Empyrean Trace","Collectibles · Empyrean Trace",null));
        RouteFile.Save(AppPaths.RoutesFile, new[] { new SavedRoute("route","Leveling example",stops,repeat,"#f472b6",leveling ? LevelingRouteStyle.Category : null) });
        return new MapTargetsService();
    }

    [Fact]
    public void WindowKeepsThreePreviousAndThreeUpcomingAcrossReload()
    {
        var service=Service();service.StartRoute("route");
        Assert.Equal(8,service.Targets.Count);
        Assert.Equal(3,service.VisibleTargets.Count);
        Assert.DoesNotContain(service.Targets,t=>t.Name.Contains("Trace"));
        for(var i=0;i<4;i++) service.AdvanceRoute("route");
        var reloaded=new MapTargetsService();
        Assert.Equal(4,reloaded.Targets.Count);
        Assert.Equal(new[]{"Main quest - NPC 2","Main quest - NPC 3","Main quest - NPC 4","Main quest - NPC 5","Main quest - NPC 6","Main quest - NPC 7"},reloaded.VisibleTargets.Select(t=>t.Name));
        Assert.Equal(3,reloaded.VisibleTargets.Count(t=>t.Completed));
        Assert.Null(reloaded.VisibleTargets[0].After);
        Assert.Null(reloaded.VisibleTargets[3].After);
        Assert.Equal(reloaded.VisibleTargets[3].Id,reloaded.VisibleTargets[4].After);
        Assert.All(reloaded.VisibleTargets,t=>Assert.Equal(LevelingRouteStyle.MainQuest,t.Color));
        reloaded.SetRouteColor("route","#f87171");
        Assert.All(reloaded.VisibleTargets,t=>Assert.Equal(LevelingRouteStyle.MainQuest,t.Color));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(14)]
    public void NearbyNpcVisitsAdvanceInOrderWithoutLeavingAndPersist(double distance)
    {
        var stops = new[]
        {
            new RouteStop("altgard", 100, 100, "Accept quest", "NPCs", null),
            new RouteStop("altgard", 100 + distance, 100, "Return quest", "NPCs", null),
            new RouteStop("altgard", 300, 100, "Next quest", "NPCs", null)
        };
        RouteFile.Save(AppPaths.RoutesFile, new[] { new SavedRoute("route", "Nearby quests", stops, Category: LevelingRouteStyle.Category) });
        var service = new MapTargetsService();
        service.StartRoute("route");
        service.Arrive(service.Targets[1].Id); // later stops still cannot be completed out of order
        Assert.Equal(new RouteProgress(0, 3), service.GetRouteProgress("route"));
        var now = DateTimeOffset.UtcNow;
        var armed = new HashSet<string>();
        var first = Assert.Single(ExplorationArrivalService.ArrivedTargets(service.Targets, new("altgard", 100, 100, now), 15, armed, now));
        service.Arrive(first);
        Assert.Equal(new RouteProgress(1, 3), service.GetRouteProgress("route"));
        var reloaded = new MapTargetsService();
        now = now.AddMilliseconds(100);
        var next = Assert.Single(ExplorationArrivalService.ArrivedTargets(reloaded.Targets, new("altgard", 100, 100, now), 15, armed, now));
        reloaded.Arrive(next);
        Assert.Equal(new RouteProgress(2, 3), reloaded.GetRouteProgress("route"));
        Assert.Equal(new RouteProgress(2, 3), new MapTargetsService().GetRouteProgress("route"));
        Assert.Empty(ExplorationArrivalService.ArrivedTargets(reloaded.Targets, new("altgard", 100, 100, now), 15, armed, now));
    }

    [Fact]
    public void LevelingArrivalStillRequiresFreshFinitePositionOnTheCorrectMapAndEnabledRange()
    {
        var target = new MapTarget("head", "altgard", 100, 100, "Quest", "NPCs", null, RouteId: "route", Leveling: true);
        var now = DateTimeOffset.UtcNow;
        var armed = new HashSet<string>();
        foreach (var position in new PlayerPosition[]
        {
            new("poeta", 100, 100, now), new("altgard", 116, 100, now),
            new("altgard", 100, 100, now.AddSeconds(-3)), new("altgard", 100, 100, now.AddSeconds(1)),
            new("altgard", double.NaN, 100, now), new("altgard", 100, double.PositiveInfinity, now)
        })
            Assert.Empty(ExplorationArrivalService.ArrivedTargets([target], position, 15, armed, now));
        Assert.Empty(ExplorationArrivalService.ArrivedTargets([target], new("altgard", 100, 100, now), 0, armed, now));
        Assert.Equal(new[] { "head" }, ExplorationArrivalService.ArrivedTargets([target], new("altgard", 115, 100, now), 15, armed, now));
    }

    [Fact]
    public void FinishKeepsHistoryRepeatAndClearResetIt()
    {
        var service=Service();service.StartRoute("route");
        for(var i=0;i<8;i++)service.AdvanceRoute("route");
        Assert.Empty(service.Targets);Assert.Empty(service.ActiveRoutes);
        Assert.Equal(3,service.VisibleTargets.Count);
        Assert.All(service.VisibleTargets,t=>Assert.True(t.Completed));
        service.SetRouteRepeat("route",true);service.StartRoute("route");
        for(var i=0;i<8;i++)service.AdvanceRoute("route");
        Assert.Equal(8,service.Targets.Count);
        Assert.Equal(3,service.VisibleTargets.Count);
        Assert.DoesNotContain(service.VisibleTargets,t=>t.Completed);
        service.AdvanceRoute("route");service.Clear();
        Assert.Empty(service.VisibleTargets);
        Assert.Empty(new MapTargetsService().VisibleTargets);
    }

    [Fact]
    public void OrdinaryRoutesKeepAllStopsAndTheirSingleColour()
    {
        var service=Service(leveling:false);service.StartRoute("route");
        Assert.Equal(9,service.VisibleTargets.Count);
        Assert.Contains(service.VisibleTargets,t=>t.Name.Contains("Trace"));
        service.SetRouteColor("route","#f87171");
        Assert.All(service.Targets,t=>Assert.Equal("#f87171",t.Color));
        service.Arrive(service.Targets[0].Id);
        Assert.Equal(8,service.VisibleTargets.Count);
        Assert.DoesNotContain(service.VisibleTargets,t=>t.Completed);
    }

    [Fact]
    public void CanNavigateBackBeyondTheVisibleHistoryAndAcrossReloads()
    {
        var service = Service();
        Assert.Null(service.GetRouteProgress("route"));
        service.StartRoute("route");
        var ids = service.Targets.Select(t => t.Id).ToArray();
        for (var i = 0; i < 6; i++) service.AdvanceRoute("route");
        Assert.Equal(new RouteProgress(6, 8), service.GetRouteProgress("route"));
        Assert.True(service.GetRouteProgress("route")!.CanGoBack);
        for (var completed = 5; completed >= 0; completed--)
        {
            service = new MapTargetsService();
            service.RewindRoute("route");
            Assert.Equal(new RouteProgress(completed, 8), service.GetRouteProgress("route"));
            Assert.Equal(ids[completed], service.Targets[0].Id);
            Assert.Equal(completed, service.Targets[0].StopIndex);
            Assert.Equal(Math.Min(completed, 3), service.VisibleTargets.Count(t => t.Completed));
            Assert.Equal(Math.Min(8 - completed, 3), service.VisibleTargets.Count(t => !t.Completed));
        }
        Assert.False(service.GetRouteProgress("route")!.CanGoBack);
        service.RewindRoute("route");
        Assert.Equal(new RouteProgress(0, 8), service.GetRouteProgress("route"));
        Assert.Equal(ids, service.Targets.Select(t => t.Id));
    }

    [Fact]
    public void CompletedRouteCanBeRewoundAndFinishedAgain()
    {
        var service = Service();
        service.StartRoute("route");
        for (var i = 0; i < 8; i++) service.AdvanceRoute("route");
        service = new MapTargetsService();
        Assert.Equal(new RouteProgress(8, 8), service.GetRouteProgress("route"));
        Assert.False(service.GetRouteProgress("route")!.CanGoNext);
        service.AdvanceRoute("route");
        Assert.Empty(service.Targets);
        service.RewindRoute("route");
        Assert.Equal(7, service.Targets[0].StopIndex);
        Assert.Contains("route", service.ActiveRoutes);
        service.AdvanceRoute("route");
        Assert.Equal(new RouteProgress(8, 8), service.GetRouteProgress("route"));
        Assert.Empty(service.ActiveRoutes);
        service.Clear();
        Assert.Null(service.GetRouteProgress("route"));
        Assert.Null(new MapTargetsService().GetRouteProgress("route"));
    }

    [Fact]
    public void LegacyTrimmedHistoryRestoresEarlierStopsEvenWithRepeatedNpcCoordinates()
    {
        var stops = Enumerable.Range(0, 8).Select(i => new RouteStop("altgard", 100, 100, "NPC", "NPCs", null)).ToList();
        RouteFile.Save(AppPaths.RoutesFile, new[] { new SavedRoute("route", "Repeated visits", stops, Category: LevelingRouteStyle.Category) });
        var retained = Enumerable.Range(3, 5).Select(i => new MapTarget($"old-{i}", "altgard", 100, 100, "NPC", "NPCs", null,
            After: i == 3 ? "expired" : $"old-{i - 1}", RouteId: "route", Leveling: true, Completed: i < 6)).ToList();
        JsonFile.Save(AppPaths.TargetsFile, retained);
        var service = new MapTargetsService();
        Assert.Equal(new RouteProgress(6, 8), service.GetRouteProgress("route"));
        Assert.Equal("old-6", service.Targets[0].Id);
        Assert.Equal(Enumerable.Range(3, 5), service.VisibleTargets.Select(t => t.StopIndex!.Value));
        for (var i = 0; i < 6; i++) service.RewindRoute("route");
        service = new MapTargetsService();
        Assert.Equal(Enumerable.Range(0, 8), service.Targets.Select(t => t.StopIndex!.Value));
        Assert.Equal(new RouteProgress(0, 8), service.GetRouteProgress("route"));
        Assert.Equal("old-3", service.Targets[3].Id);
    }

    [Fact]
    public void StableIndicesSurviveQuestNamesAndPositionsBeingCorrected()
    {
        var service = Service();
        service.StartRoute("route");
        for (var i = 0; i < 5; i++) service.AdvanceRoute("route");
        var route = service.Routes[0];
        RouteFile.Save(AppPaths.RoutesFile, new[] { route with { Stops = route.Stops.Select(s => s with { Name = "Correct quest", X = s.X + 15 }).ToList() } });
        service = new MapTargetsService();
        Assert.Equal(new RouteProgress(5, 8), service.GetRouteProgress("route"));
        Assert.Equal("Correct quest", service.Targets[0].Name);
        Assert.Equal(615, service.Targets[0].X);
        service.RewindRoute("route");
        Assert.Equal(4, service.Targets[0].StopIndex);
        Assert.Equal(515, service.Targets[0].X);
    }

    [Fact]
    public void RemovingAFutureVisibleStopSkipsThroughItAndRemainsReversible()
    {
        var service = Service();
        service.StartRoute("route");
        service.Remove(service.VisibleTargets[2].Id);
        Assert.Equal(new RouteProgress(3, 8), service.GetRouteProgress("route"));
        Assert.Equal(new[] { 0, 1, 2 }, service.VisibleTargets.Where(t => t.Completed).Select(t => t.StopIndex!.Value));
        service.RewindRoute("route");
        Assert.Equal(2, service.Targets[0].StopIndex);
        service.AdvanceRoute("route");
        Assert.Equal(3, service.Targets[0].StopIndex);
        var later = service.Targets[2];
        Assert.False(service.Toggle(later.MapId, later.X, later.Y, later.Name, later.Kind, later.Icon));
        Assert.Equal(new RouteProgress(6, 8), service.GetRouteProgress("route"));
        service.StartRoute("route");
        Assert.Equal(new RouteProgress(0, 8), service.GetRouteProgress("route"));
        service.DeleteRoute("route");
        Assert.Null(service.GetRouteProgress("route"));
        Assert.All(service.Targets, t => Assert.Null(t.StopIndex));
    }

    [Fact]
    public void BackwardNavigationDoesNotChangeOrdinaryRoutes()
    {
        var service = Service(leveling: false);
        service.StartRoute("route");
        service.AdvanceRoute("route");
        var remaining = service.Targets;
        service.RewindRoute("route");
        Assert.Equal(remaining, service.Targets);
        Assert.Null(service.GetRouteProgress("route"));
    }

    [Fact]
    public void UnnamedWaypointKeepsItsMarkerWithoutDrawingAnInstructionLabel()
    {
        var placement = new MapPlacement("altgard", new MapFix(1, 0, 0, 0, 1, 0, 50, 60, 0.3, false), 1,
            new Rectangle(0, 0, 700, 400), new Point2d(30, 200), WorldMap: true);
        var waypoint = new MapTarget("waypoint", "altgard", 200, 180, "", "Waypoint", null,
            Color: LevelingRouteStyle.MainQuest, Leveling: true);
        using var image = RouteOverlayForm.Draw(placement, [(waypoint, 0)]);
        Assert.True(image.GetPixel(212, 180).A > 0);
        var labelInk = 0;
        for (var x = 225; x < 500; x++)
            for (var y = 155; y < 210; y++)
                labelInk += image.GetPixel(x, y).A;
        Assert.Equal(0, labelInk);
    }

    [Theory]
    [InlineData("Main quest - Hans","NPCs · Regional Quest","#facc15")]
    [InlineData("Side quest - Hans","NPCs · Hero Quest","#4ade80")]
    [InlineData("Main quest - Clear Stronghold","Locations · Stronghold","#ffffff")]
    [InlineData("Sealed Dungeons (kill boss)","Waypoint","#ffffff")]
    [InlineData("User Teleport (7)","Waypoint","#a78bfa")]
    [InlineData("Open Telepor","Waypoint","#a78bfa")]
    [InlineData("Camp","Locations · Kibelisk","#a78bfa")]
    [InlineData("Niror","NPCs · Regional Quest","#4ade80")]
    public void ObjectivePriorityMatchesTheInstructions(string name,string kind,string expected) =>
        Assert.Equal(expected,LevelingRouteStyle.ColorOf(new RouteStop("altgard",1,1,name,kind,null)));

    [Fact]
    public void OverlayDimsPastPointsAndTintsObjectiveSymbols()
    {
        var placement=new MapPlacement("altgard",new MapFix(1,0,0,0,1,0,50,60,0.3,false),1,new Rectangle(0,0,700,400),new Point2d(30,200));
        var past=new MapTarget("past","altgard",100,100,"Past","NPCs",null,Color:"#a78bfa",Leveling:true,Completed:true);
        var next=new MapTarget("next","altgard",350,200,"Next","NPCs",null,Color:"#facc15",Leveling:true);
        using var icon=new Bitmap(20,20);
        using(var g=Graphics.FromImage(icon))g.Clear(Color.Red);
        using var image=RouteOverlayForm.Draw(placement,[(past,0),(next,1)],resources:[new(100,100,icon,past.Color,0.28f),new(350,200,icon,next.Color)]);
        var faded=image.GetPixel(100,100);var bright=image.GetPixel(350,200);
        Assert.True(faded.A<bright.A);
        Assert.True(bright.R>200 && bright.G>150 && bright.B<80);
        Assert.Equal(0,image.GetPixel(55,164).A); // history must not draw a line back to the player
        if(Environment.GetEnvironmentVariable("SOULCREST_ROUTE_UI_PREVIEW") is { Length: >0 } output)
        {
            Directory.CreateDirectory(output);image.Save(Path.Combine(output,"leveling-overlay.png"));
        }
    }
}
