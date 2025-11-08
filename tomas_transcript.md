Tomas Grosup - May 19
VIEW RECORDING - 71 mins (No highlights): https://fathom.video/share/zCBCFwXxhtj8xg478o1mSfJ15dCvPCQK

---

0:00 - T-Gro (Microsoft)
  Oh, hello. Yes, I can.

0:02 - Nathaniel Elkins (nelknet.com)
  All right.

0:03 - T-Gro (Microsoft)
  Hi. Nice to meet you.

0:05 - Nathaniel Elkins (nelknet.com)
  Yeah, likewise. Thanks for taking the time to meet. And yeah, just point me in the right direction. You know, this is something that I've kind of been interested in for a long time, and I find myself with a bit more free time on my hands.  So I thought, you know what, this is something that the community has been professing a desire for for a long time.  So why don't we take some steps to move it forward? And I got like a very minimal something working.  So maybe I thought I would walk you through that. And yeah, let's just see where it takes us and see if we can't make some incremental progress, right?  Best time to start, this would have been five years ago, but second best time is today.

0:49 - T-Gro (Microsoft)
  So thank you for being the person who is brave enough to step in where no one else was willing to step before.  Or a few people try to look at it. And And ... ... ... Yeah, you are right, this is very high.  think it's the top one or two on the F# issues when it comes to upvotes. And especially for people who build web apps, this is a missing piece, because they do have, like, reload with Fable for the, like, front-end stuff, but the, yeah, the .NET backend part is what's missing.

1:20 - Nathaniel Elkins (nelknet.com)
  And it's not just web apps. You know, I've taken a crack at doing mobile apps with F# a couple times.  I actually had a previous job, you know, doing some Xamarin work. And, well, back when it was Xamarin, was a long time ago now.  But, yeah, quite painful. It was kind of working for a second there when Don had put together the port-a-code thing, and then the people, the fabulous guys, they had kind of, like, rigged something up using port-a-code, which was using the interpreter.  But it was super fragile, and they basically dropped support for it. So, anyway, yeah, everyone's been wanting that. And by the way, let me just tell you about myself real quick.  sort of know who you are because, you know, you're the face of F# now at this point, more or less.  Let's see. I'm an F# enthusiast. And like F# has actually been kind of a big deal for my career in the sense that my first, not my first, but an early job I had was working at Jet, you know, Jet.com.  And that was like transformative for my career. And that was back in 2016. And basically like every, almost every job I've had since then has had like some connection to that community.  And, you know, I've worked either closely or somewhat distantly with a lot of different F# people. Actually, at my last client job, I was working with Will Smith.  I have, like, have worked with Eric Sarpolis a little bit. I'm reasonably close. Um, Gus, the guy who wrote, uh, you know, F# Plus, and, uh, I actually worked with Paul, like, for, I don't know, whatever.  He worked on my team at Jet for, like, six months or something like that. So, you know, it's, like, kind of been bouncing around the whole F# ecosystem and working with the same people since 2016, basically.  So, anyway, oh, and so now what am I up to? Uh, about a year ago, actually 10 months ago, I decided to try and do solo consulting.  So I've been doing that for, yeah, 10 months now and been able to survive doing that. And so I just had a big client contract end, so I have a little bit of time on my hands.  And, um, yeah, that's kind of what I'm up to.

3:43 - T-Gro (Microsoft)
  No, I'm happy to hear that the small but very connected F# community works in, like, bringing people's professional lives together.  Yeah, I all, all know of these names, including you. are, like, the heart of the active F# community. And it's also the reason.  That's we do it, right? Because if it wouldn't be for the vibrant community, then there wouldn't be much, much left for F# nowadays.  So it's really good that you brought up all these names because you are all our heroes.

4:18 - Nathaniel Elkins (nelknet.com)
  Yeah, we're all just trying to, you know, move it forward. Cool. So let me just share my screen and I'll maybe make sure we're just like totally on the same page.  And you can kind of point me in the right direction. So I, you know, I've never written a compiler before.  I don't have that much experience with the compiler, except, you know, I follow the F# compiler repo closely. So, and I, you know, I watch every amplifying F# whenever it comes out, you know, but I haven't actually done that much work.  But I have like a general awareness of how everything works and what's going on. So first, let me run my little proof of confidence.
  SCREEN SHARING: Nathaniel started screen sharing - WATCH: https://fathom.video/share/zCBCFwXxhtj8xg478o1mSfJ15dCvPCQK?timestamp=299.022565  Also, I'm not as fully set up as I would have liked to have been because I was actually doing that work on a computer that a client gave me, and I had to give it back because my contract just ended.  So this is a new computer that I'm getting set up with. So I don't have all my tools set up.  Don't tell them. Okay. So a lot of this has been, you could say, vibe-coded. And I'll kind of tell you how I started.  I actually started... The thing that inspired me to really take the plunge, I mean, obviously, I've been commenting on that Hot Reload issue for a long time, trying to poke people and move it forward because I didn't think I was, you know, new enough to move it forward.  But then, let's see, how Hot Reload works. There was a YouTube video, actually. It's on the, let's see, Hot Reload GitHub.  Whoops. Whoops. I'm sure. Let's take a look at that. This guy. And then it was kind of a recent one.  If go to the bottom here, right? Yeah, I've been talking on this one for a long time. And blah, blah, blah, blah, blah, blah.  Okay, this one right here. This was the one where this guy, like, let me know enough about how this thing was working that it kind of let me get started.  And this is probably a useful video for you to watch as well. Actually, all the material that I dropped in that thread is going to be very useful.  But so maybe I don't know how much you know about this, but like a big part of the whole hot reload thing is basically there's this thing called like the it's part of the like the ECMA spec, right?  Like the common, the CIL spec, which is called the ENC log and the ENC map, which are basically, you know, that's for edit and continue.  And everything's kind of based on that. And the runtime exposes a method called apply update, which is implemented both in mono and on the .NET runtime.  And it basically takes three streams of bytes, a metadata update, which is responsible for updating this ENC map and ENC log.  It takes an update for like IL bytes that need to be updated. And it takes a stream of PDB bytes, which I think actually is optional.  That's for debugging, and that's optional. And this is like a great video where this guy, David, explains kind of how the metadata tables work.  Maybe you know all this, but this was news to me about the format and all this stuff and just have it all maps together, yada, yada, yada.  And then also the other thing that was super useful was he made me aware of this tool, which was also extremely important for me to basically try and guess and check.  Because this tool was a way for me to validate that. My IL bytes and metadata bytes were at least in the right format because that was like a really tricky part.  And that was the tricky part of that guy, was his name Spencer or something like that. He tried this a couple of years ago, but he couldn't get the right format.  This guy, Spencer Parker. So, yeah, he had taken a crack at this. Also, the other thing that he was doing is he was trying to go through a lot of the C# infrastructure, and I wasn't trying to do all that.  I wasn't trying to integrate with .NET Watch, and you actually don't need to, as I've shown. You don't need to do any of that.  There's really just one method called apply update, and if you can get your bytes in the right format and apply that thing, then it'll work.  Okay? Doesn't mean it'll work well, but it will work. Okay. So, by the way, feel free to interrupt me if you think I'm rambling here.  Okay. So then what did I do? Okay. Well, I started this little repo. So then I cloned a bunch of other repos.  So You know, the F# repo Roslyn runtime, and I've basically been using cursor and throwing a bunch of money at it with AI coding tools to give me the context I need.  And it's actually been super useful. I don't know the extent to which you're using these tools, but, like, I can literally reference the entire ECMO spec here, and it'll just import it, and I actually have to give it a better model once I can go, please.  Oh, you know what? This isn't another thing that I need to set up. I don't think I set up my—have I even signed into this thing?  Let's see. Cursor. File. Let's go to settings. Cursor settings. Oops. Let's go to this. So I should prepare for this a bit more.  General Pro. No, I am logged in here. Oh, did they? Oh, no. That's why. Let's go to Pro and Max.  Yeah. Okay, great. And if I was just saying, like, you know, tell me. About the ENC log and ENC map from the ECMA spec, describe it, and I'm going to minimize this, actually, and this.  It in detail, you know, it'll take some time, but this will, like, this kind of thing has been just amazing for getting me to unblock things, and, right, like, it tells me the section, tells me everything, gives me the bytes, and I just have been going back and forth, and this is unblocked, like, a huge amount.  It's kind of expensive when you're using the max context, and, you know, I probably spent, like, 50 bucks in total over the course of, like, a couple, you know, I don't know, a couple days, just chucking huge amounts of the runtime context at it, like, you know, millions of lines of code or something, but anyway, so far that's been working.  Okay, so I guess I'll just go into the little. Test that I have. So setting this environment variable, the runtime looks for this.  This is going to be required in order for the runtime to allow the DLLs to be patched, okay? Then I've got this little test here, and you can ignore this framework load thing was just because I had multiple versions of .NET running, you know, side by side and whatever.  It was resolving to the wrong version. You can ignore that. But it's basically an assembly load context, and what it does is it compiles a little bit of code like this, which you can change it.  It takes this little bit of code, it compiles it, and then... And then it basically finds the member and then tries to update it.  And, okay, so here we go, right? We create an assembly load context. Don't worry about this. It's basically just an assembly load context that just makes sure that when it's system.runtime is resolving, it's resolving to the thing that I have on my computer.  Then it just prints a bunch of stuff about it. Then it, let's see, gets some info about that particular method that we compiled there.  Oh, then it invokes. Okay, so then this thing is, it invokes it. So we have the DLL that we compiled from that snippet, and it invokes it via reflection.  Again, a lot of this was vi-voted, so I know it works, although it might like be, might have a bunch of extraneous.  Stuff here. was just throwing things at a wall to see what would stick. Okay, then this is me just printing some stuff about the metadata tokens of what I was invoking.  Again, this is basically just all debug information. The module version ID, this needs to be the same between invocations, or when you're generating your metadata information, it needs to be the same.  Again, more debugging, blah, blah, blah. Okay, so then, basically, I pass in the original assembly, and then I've got this generate delta logic in here.  Let's see if can go to that. And this is where, you can say, I cheated, where I was not used, the ideal thing, of course, is to have two, you could say, like, or, like a...  Compilation, like some kind of stateful compilation, doesn't have to be stateful, but some kind of like compilation instance where you pass in new F# code, and from the previous code, it generates new stuff, and then that gives you the delta in the C# land.  This method is called commit difference, but I didn't do that. I basically, I think this is actually a bunch garbage, but I can do the method to update, and maybe go up here.  Basically, I just generate the new IL from scratch, and there's some notes here about the format of what everything is supposed to be, but yeah, this generates the, based on whatever return value is put in here, it generates the IL in the right format, right, delta format.  Okay, that's what that emits over there, and then. And this part was, this actually is not important, the PDB delta, or it's not that it's not important, but it's just not required, but it's really the, here's the metadata, delta, delta, delta generator, create, that's fine.  Okay, this one was really the one that was quite tricky, you know, the MVID needs to match, and then like some other, there's this other ID called an ENC ID that needs to match, and like some of these things are like, for example, the actual logic of this is kind of in conflict, is in conflict with the ECMA spec, and I found all this out by using this tool, MDV, which basically you pass it a DLL and then some,  You pass it a DLL and then some IL or metadata delta files, and then it tries to apply them.  And the way that I got that in the right format was looking at this other thing, hot reload utils, okay, which is this thing.  And by the way, like the .net or the, sorry, the NuGet publishing pipeline for both of these are kind of broken, so I had to install them locally, but there's a little tool in here.  Which you can use as either a global tool, or I think actually originally I was, I haven't actually gotten this working, although I'm imminently about to get it working.  And I used it as like the generator version where I added it as some kind of MS build task.  But basically, the way that works is, if you give it something like in this format where you... If you have to include something that looks like this, you give it the documents, and it will generate a DLL and then the ILL files, okay?  And when you run the Metadata Viewer tool, MDV, I'm just going actually run this for you. So let's see where I'm at right now.  Okay, here. Let's see if this works. I'll show you when MDV actually starts in here. Okay, MDV is all of this stuff right here, starting here, okay?  It basically outputs something that says, okay, generation zero, and if you just run MDV against a DLL without any ILL things, it'll basically print everything from here through everything that's in generation zero, okay?  So like through to here, okay? And you're inspecting this, and you're looking at all these things. You're looking at byte offsets, et cetera.  But hey, thank goodness, whatever we're producing today with the F# compiler... Like, is in the right format, and this worked from the outset for generation zero, has the right name, okay?  And you notice that the ENC ID and the ENC base ID are nil in the beginning. It's got this MVID, and then, you know, this is, like, all the metadata tables that was talked about in that presentation, okay?  A bunch of blob stuff. Again, like, if you start, when you start working with this, and maybe you know all this already, but this was all news to me.  About all the stuff that's in here. And then offsets, and where in the blob it is, and again, back in the spec talks about all this, and you can just chat with it to figure out what's going on.  Okay, so then we get generation one, and then it references all of these things. And this is kind of, like, a reference to, you if we were looking at 270, right?  This is some string, so it says, like, number, what, 270, and it's probably coming from... ... If we look for zero.dll, you can see it's like referencing one over here in the string section of the metadata table or headers or whatever it is.  Then there on the first one, we now have an edit and continue ID. The base ID is null, but then going forward on subsequent updates, I haven't actually done this, but I'm pretty sure that the ENC ID will now become the ENC base ID, which is the first one, and then on the next edit, there'll be a new ENC ID.  The MVID is the same, all these things, okay, and it basically has all the things that have or the pieces that you needed to give the runtime to change.  Again, this was via a lot of trial and error to figure out, hey, what things do I actually need to place?  And it wasn't total trial and error. Like you can see here, this snippet, if I can get this, this snippet here is very similar to the one.  And so what I did is I generated the delta for this thing, where I generated multiple deltas here, and I just looked at what changed and what were in generation 0, 1, and 2 when I was running this with hot reload util to generate the new things.  And then I just made sure that that was similarly being generated for F#, like, okay, this type ref thing needs to be updated.  Obviously, the method needs to be updated. And then the ENC log gets updated, and then the ENC map gets updated, and this also needs to be updated.  So, yeah, basically, that's where I'm at. And then I got it working. So, okay, so that was my little success story for everything I got working.  So the next thing I'm going to talk about are... Are the current state of things in the F# compiler, and maybe some directions we can take, but I'm going to pause here and ask if you have any questions or anything like that.  Hopefully we haven't fallen asleep.

21:12 - T-Gro (Microsoft)
  Yeah, yeah, yeah, sure. Thanks. So if I got it correctly, you reverse engineered what Roslyn is doing, and you turned that into a format which I was expecting, which is based on the IL format, but it's a dedicated format just for the deltas.  It, however, has many similarities with the format for, like, static IL. It just has some special additions in the metadata tables or references so that the format for deltas can reference existing rows in the IL tables, and you applied it with, like, hard-coded or, like, manual handwritten delta.  So you are, I guess, Your next step is how to detect the Delta programmatically, because right now you are able to hand throw Delta via API calls, and you are able to apply the Delta which you hand throw via the metadata builder.  Yeah, exactly. Am I like that?

22:15 - Nathaniel Elkins (nelknet.com)
  Yep, yep, that's absolutely correct. And then, let me just find, I just want to show, I mean, it's super simple, but this metadata updater, so this is the method that actually applies the Delta.  Like, this is all you need to do to the runtime. You basically, you give it an assembly, and then...

22:37 - T-Gro (Microsoft)
  And free batteries.

22:39 - Nathaniel Elkins (nelknet.com)
  Exactly. So it's actually, like, the actual application, in some ways, is very simple. And the way, and by the way, the way that this is, this, you could say hooks in, this is detailed in, let's see, in this blog post here, where basically there's, all right.  This is interesting, but how it actually hooks in is, oh, wait, where's the, it uses the thing .NET startup hooks, .NET, okay, startup hooks, yeah.  Basically, there's, this environment variable gets set, and I think it gives, like, a semicolon separated list of methods to execute.  Like, if you were to search for this .NET startup hooks, let's, I don't actually know what this is, basically, yeah, you can, like, call some DLL, which calls an initial, it basically requires an initialize, actually, I think it'll probably be in here.  Okay, yeah, yeah, yeah, this basically gives you some startup hooks, and basically, you add it or set it to the, you have a DLL in here that has an initialize method.  I think it may actually need. Basically, it looks like here. And then, you know how before I was getting the assembly, if you're wondering, like, hey, how do I actually, you know, forgetting about debugging for a second, that's a totally separate thing.  But just, hey, how would I get this running as part of a workflow? Like .NET Watch, I think, is using something like this, where it makes, it, like, modifies the .NET startup hooks, makes sure this thing gets initialized, and makes sure, you know, it has, like, file watchers and some other stuff.  That's how that works. Anyway, I just wanted to note that of how it actually all gets kicked off. But, yeah, otherwise, yeah, you're totally spot on about how it all works, and, yeah, exactly what the next steps are.

24:50 - T-Gro (Microsoft)
  And when it comes to the F# side, I guess you already came across the problem that most of the IL parts in F# are self-written.  And not using the system-provided types from the packages, which means the existing encoding of all the F# metadata was hand-rolled by Don Saim years ago, and you cannot reuse what Roslin is doing, because Roslin is actually using the system packages.  They can share some of the logic between, like, a static write and the delta applications, whereas in F#, this needs the deep context of the full ilwrite.fs, because the ilwrite.fs is basically the de facto documentation how F#'s IL representation maps to bytes.

25:39 - Nathaniel Elkins (nelknet.com)
  Yes, yes, that's absolutely correct. On the other hand, so I keep kind of, like, flip-flopping back and forth. On the one hand, I think, damn, it would be easy to use that metadata builder, you know, both the metadata builder and the metadata reader to set all this up, and you could look at Roslin for how things are done.  Which I have. On the other hand, you know what, again, with all these AI tools, like, this is reasonably well designed, and it's kind of convenient that this is all in one file.  You know, one thing, like, I remember when I would look at these files on GitHub, I'd be like, oh my god, this is demented.

26:23 - T-Gro (Microsoft)
  Who puts, like, you know, 5,000, you know, 4,600.

26:26 - Nathaniel Elkins (nelknet.com)
  But then, you know, as I was going through it, I was kind of thinking, you know, actually, like, it's all in one file.  The thing about when I'm opening this up in, or when I'm opening up Roslyn in, say, Rider, and I'm, you know, like, bopping around to six different files.  around. Yeah, exactly. So, you know, anyway. And then you start to look at the code between them, between what Roslyn has.  Although, I'm trying to remember if, I think, I'm trying to remember if metadata reader is part of the runtime repo.  Here, let's take a look. Meta, metadata, builder.

27:05 - T-Gro (Microsoft)
  Yeah, I think it's system.reflection.metadata, and they have, like, writer, reader, builder.

27:12 - Nathaniel Elkins (nelknet.com)
  Yeah, it's probably in here, so I'm going to take this out, and system.metadata. Well, like, something in here. Whatever.  And this is the reader, not the writer. But anyway, long story short, mean, you can see there's, like, million different files here, et cetera.  I mean, we kind of need to, like, map these different types between them, right? And, you know, this is a good reference.  You know, anyway, like I said, or I was about to say, I keep on going back and forth between, oh, do we rewrite this and make it have some kind of, like, slightly more functional interface, because right now this is just a giant back.  At the same time, you know, it's kind of clear, and then, you know, maybe, I don't know, like, I don't really think there's too much, like, super, super specialized and sharp stuff in here.  I mean, specifically on the metadata table writing, like, it's kind of well-organized. Okay, you got the tables here, you've got all these other things here, and if there's a bunch of, like, rewriting it seems like so, so much, as opposed to altering it, because the whole thing really is, for the most part, not entirely, like, there's some kind of, you could say, fix-up passes at the end of this thing.  But I feel like this thing could be modified to somehow either be, like, the process itself wouldn't be stateful, but that...  It could be modified to potentially create deltas. So that's kind of my next, well, I mean, I guess I should talk with you about this, but I'm wondering, like, hey, what do you think about that?

29:12 - T-Gro (Microsoft)
  Or do you think that's a lost cause? So looking at the, like, full compilation pipeline, I would, like, choose two appropriate levels.  Because the compiler goes from lexing, to parsing, to type checking, to ILS chat, which is still internal representation, and then to ILL write.  And one level is for actually producing the ILL div, so really the byte streams, which ILL write is the closest possible level, I guess, the best one to use as context and to modify.  But the first one, and I think this should be a different level, is the one in which to detect the delta in the first place.  And a way I would suggest is... To look at the type tree and the type tree to ILX-gen transformation, because both of these are like tree representations, and a tree representation is very well suited for detecting a delta, because you have the two trees, and on every level you can detect was this not changed at all, was it added, updated, or removed, and you can like recursively traverse the tree structure.

30:26 - Nathaniel Elkins (nelknet.com)
  Yeah, well...

30:28 - T-Gro (Microsoft)
  And I don't think that IL-right, the binary one, is appropriate for this delta, because it already loses information on the F# specifics, right?

30:37 - Nathaniel Elkins (nelknet.com)
  Because at that level, you no longer know what was a discriminated union, and what was the closure, like it's all just like bytes, or it's close to the IL level, whereas I think the delta detection is better done on a level which still understands F#.  Hmm, I see, I see. I guess my thought for this was, um, well, let's, like, what's, I'm trying to think, like, what...  This is the entry point to, I'm trying to find the entry point to IL right, because here's the thing, I mean, with regards to detecting the, you could say detecting the difference, so to speak, is more like, I mean, maybe I, I don't want say we don't care about anything that's F# specific.  Obviously we do, like the thing that I want to be able to do is, if I have two separate compilations, then like, maybe figuring out, hey, maybe I can just look at the two of them, and figure out what it should be from the, from the Delta format.  Are you saying, like, actually, it is super important to know about, like, you know, all the things that have very...  So that can be incorporated into the, like, maybe either like a new type or find all the symbols or whatever it is that changed and then emit IL for those things specifically.  I guess it kind of depends on what the entry point to this IL write is at this point. Like, is it the ILX?  This is the internal representation. You know, what's, I guess, what's the level that we could find the diff at?

32:28 - T-Gro (Microsoft)
  Does that make sense? Yeah, ILX is a superset of IL, which was invented by Don Syme to make it easier to build functional language compilers for .NET.  Nowadays, F# is the only one, but ILX has some additions on top of IL and has a mechanism how to lower those additions, such as unions and closures, into IL.  Because regular IL doesn't know about closures or unions. But don't, don't have the idea that many. The language implementations will share the same, like, low-level specifics, so it's a superset based on it.  And why I think that the, like, the real ILL level might not be appropriate is because F# does have a lot of compiler-generated code.  If you've ever debugged an application, you, like, you might have noticed that when you have, like, a closure, there is an add sign, and then there is a line number added to the function name when you debugging.  Like, you can have a number which is, like, my function add, and then there is 520. And when you just add a few lines on top of the file, all of your compiler-generated method names will get renamed because they shift by the number of lines above them.

33:43 - Nathaniel Elkins (nelknet.com)
  Right.

33:44 - T-Gro (Microsoft)
  And this transformation happens when, like, moving from the F# specifics to the ILX gen and then to IL. So they, they, they, basically, the the order of things.  Is that after type checking is over, what we have is a type 3, which is again in a single file called like type 3.fs, where you have all the unions describing the expressions in F# way.  After that, the ILX gen lowers that into a common representation, which is not yet IL, but it's very similar to IL.  It's no longer expressions, but for example, the implementation of method bodies is already a stack-based language. So really the contents of the bodies are already converted to a stack-based assembler-like language.  And then the IL write takes that and changes it into a stream of bytes and changes the representations from like a graph of in-memory objects, because the ILX gen is still objects in memory, which are like connected, and the IL writing serializes that into tables and streams of bytes, which can be written to disk.

35:00 - Nathaniel Elkins (nelknet.com)
  Got it.

35:01 - T-Gro (Microsoft)
  the I'll write is suitable for writing. It does have stuff like taking care of having a dedicated string table, reusing strings, so when you have the same constant string in your code, it still exists only once in the metadata, and making sure everything can be, like, serialized in order.  But it might be difficult to detect delta in that.

35:25 - Nathaniel Elkins (nelknet.com)
  Got it. Okay, yeah. I see what you're saying now, also this kind of reminds me that as part of this whole process, maybe we can just quickly look at, like, let's see if I can this, compilation.cs.  What is this? I hope I'm in the right repo here. This is F#. I really want...

35:51 - T-Gro (Microsoft)
  You want the Rustin one, right?

35:52 - Nathaniel Elkins (nelknet.com)
  Their main compilation object. Yeah, exactly. Why am I in F# here? Maybe I need to... There are. Thank Oh, there we go.  And I think it's this guy over here. probably, like, and, you know, I read that advice from that guy from Microsoft, TMAT.

36:19 - T-Gro (Microsoft)
  I don't actually know what his name is, or I forget, but...

36:25 - Nathaniel Elkins (nelknet.com)
  Got it. Yeah, and I was kind of following through here, and is there an emit difference? Okay, so, I mean, this is, this is obsolete, but there's some other version of this, updated methods or something like that.  Okay, this, I think this is the one that I was following through, and if you follow all these things through, I doubt these will work, because, yes.

36:47 - T-Gro (Microsoft)
  So I think that the heart of their, like, hot drill to data structure is the semantic edit, right, which is a model of the divs, and I think they also have it on the semantic tree level, and not on...

37:01 - Nathaniel Elkins (nelknet.com)
  Yeah, I think you're right, and also they have this thing called, like, I'm still trying to figure out exactly what it works, but again, this is, like, why these tools, you know, the AI is so amazing, but it's called the Symbol Matcher.  Symbol Matcher, this thing that I think also plays a very important role in determining, you know, if Symbol changed, you know, how does this map to something in the metadata table, something like that.  And this runs to determine, hey, if something changed at the higher level, what are the lower-level constructs that need to change?  So, you know, there's, yeah, there's a lot here. But, you know, I was doing everything just at that, like,  Like, lowest proof-of-concept level to show that it was possible in some capacity, but obviously there's a pretty long way to go for this.  Yeah. So, obviously, you know, my... everything that I was doing is totally, completely outside of the F# compiler, but if I was to add something, I mean, I guess it should probably...  mean, do you think that this should be part of, like, the F# compiler, F# compiler services, a new project?  Like, do you have any idea of where this should go? I mean, if it's accessing a lot of the...  know, a lot of these things, like, you know, the metadata builder and all that kind of stuff, that's all public in the Runtime or Csharp compiler.  But all that infrastructure is private in F#, so it's not like I can really reference these things. From, you know, like, reference F# compiler services, and then...  Thank you. You

39:01 - T-Gro (Microsoft)
  Yeah, I think that F# compiler service encapsulates the things you would need to detect the data and then emit it.  It exposes some symbols, mainly for tooling, like for IDs and so on, but you would need to either expose a lot of new APIs from F# compiler service to your new projects, or put the code inside of the compiler to have access to it.  I don't think that right now it exposes anything near to being sufficient for emitting data. I would say that by the nature of it, by being so close to the compilation pipeline and really have to understand how ILS emitted, this would need to sit inside or make some code reorganization within the compiler.  That maybe the compiler would cut out the pieces responsible for ILS gen and ILS writing, and the compiler would make a separate package out of them.  But I would say that... that... It would be simpler if this is done, like, inside the compiler repo, because then you can reorganize later, but at least you would not be blocked by accessibility of all the types and members.

40:13 - Nathaniel Elkins (nelknet.com)
  Yeah.

40:13 - T-Gro (Microsoft)
  That's my current view. Yeah, all the types, like, all the tables and stuff is written and all the constants required to put the tables together, that's not exposed, right?  Those are all internal modules or some type, yeah.

40:27 - Nathaniel Elkins (nelknet.com)
  Yep, yep. But, okay, that makes sense. I think, actually, the real next step for me is to write out, like, a really long and detailed PRD.  Now that I have a decent sense of how these things work, or actually, I don't have a decent sense, but I've got at least a surface-level sense.  Before I had no sense, now I've got a surface-level sense. And identifying all the parts of the C#, sort of edit and continue.  going to review. Lord. Good I'm everything. Slash, you know, hot reload infrastructure, which is going to include, okay, you know, what methods do they have in compilation and C# compilation?  How does the symbol matcher work? Like, how does all that line up, et cetera? And then make sure that there is, you can say, a matching construct, or just identify them so that we can talk about what matching constructs should exist in F#.  So that we can, you know, start coding away and figure out what needs to change to make this happen.  There's one other thing that I wanted to talk about, which is, it wasn't entirely clear to me the extent to which, like, SIG and op data needs to be taken into account for this.  I kind of think that if you're at the point of editing a running assembly, that it wouldn't need to be used.  Just because it's only used for compilation, or it wouldn't need to be edited because, again, it's for a running assembly, and I thought that the SIG data was for type checking.  And so if you're editing live code and you write some code that can't compile or something like that, it doesn't really need to edit the SIG or OP data blobs that are in the new assembly.  But maybe either one, I'm wrong about that, and it actually is kind of important, or two, maybe we should do it for the sake of completeness or something like that.

42:46 - T-Gro (Microsoft)
  I don't know. What do you think about that? It's only needed for cross-assembly compilation. So basically the SIG and OP data, when you are building a library A, which is consumed by application, then it's needed when you compile the application.  But at the time you run it, it's not looked at runtime. So runtime does not understand that format. It's only needed when compiling a library.

43:10 - Nathaniel Elkins (nelknet.com)
  Yeah, that's what I thought too.

43:12 - T-Gro (Microsoft)
  And so I thought, okay, you know don't worry about it. I would have to think how Hot Reload handles like a graph of many projects, which depend on each other.  Because it can happen that you have a deeply connected graph and you change a utility library and you want it to propagate up.  But then I would assume it simply like reputes the library fully and does not try to take the small edit of a library, which is like five levels deep, and propagate that small edit up across multiple intermediate projects into your main application.

43:47 - Nathaniel Elkins (nelknet.com)
  Yeah, and frankly, I mean, I do think that it's one of those things. And I think this is the way that C# works too.  I mean, you wouldn't, I don't think that a developer should expect that if you do something... That changes the signature of your function, that somehow this is going to propagate in a hot reload way to patch everything else going up.  I think it should basically just be detected as a root edit, and then pause the restart.

44:20 - T-Gro (Microsoft)
  Yeah, that makes sense. And it will make the life simpler. Because there are some F# specifics. Imagine you have a library and you change a function to be inline.  So that would mean that all the code which exists above it would have to be recompiled because inlining means a different thing than just calling it directly.  But those can be refused, like you say, as root edits which require a full recompilation of the graph of projects.

44:51 - Nathaniel Elkins (nelknet.com)
  Yeah.

44:54 - T-Gro (Microsoft)
  And looking at what you have shown, I think it will be important to model the right equivalent of Roslyn’s semantic edits, and there might be some people who will have an opinion on it as well.  I would decide somewhere between the typed tree and the ILX tree, and this will be for sure a big data structure when it comes to describing it, but I hope that this is where AI can come to help.  It doesn't really matter that the existing typed tree has hundreds or even thousands of cases defined, because edits on those data structures will be similar in size, right?  So if F# has dozens of different representations—records and unions and functions and methods, each of which can be added or removed or updated—that will need to be reflected in the data representation of the semantic edit.  But with the right context, like maybe feeding it with the Roslyn way and the existing F# data structures, this representation for the edits on the underlying tree could be generated.  And maybe like you say, putting it as an open document and letting people, including us, comment and provide feedback might reveal some insights or maybe corner cases to be aware of.  And I guess in hot reload there is a fallback mechanism, right? If something is too difficult—for example, if unions change their inner representation.  Like when you have an F# union which is only one case, and you add a second one, the inner representation changes.

48:00 - Nathaniel Elkins (nelknet.com)
  Types set, but they want to change the logic internally. So they're not necessarily changing the types, but they're changing how they're being used.

48:09 - T-Gro (Microsoft)
  basically, like, method. So function bodies, right?

48:14 - Nathaniel Elkins (nelknet.com)
  Yeah, function bodies, exactly.

48:17 - T-Gro (Microsoft)
  That should also be one of the straightforward ones to verify. Because if you only change a function body, and it isn't a function which gets converted to many closures, that would still have the same metadata, it's still the same types, methods, so you don't have to mess with the headers.  And you should be able to replace the IL, the assembler, the sequence of instructions, the old one with the new one, which is the level that ILXGen has.  So in ILXGen, you already have an F# representation of all the instructions, but it's not yet a stream of bytes.  It's still, like, members of a dedicated discriminated union. I'm not sure if you have seen it.

49:00 - Nathaniel Elkins (nelknet.com)
  In the compiler code base. This guy, right?

49:03 - T-Gro (Microsoft)
  So ILXGen, this guy is the one which does the conversion. And you can see if you navigate to one of the instructions, was...  Oh, too fast for me.

49:16 - Nathaniel Elkins (nelknet.com)
  Oh, sorry.

49:19 - T-Gro (Microsoft)
  Try to search for something like LDR, or some instruction name, or LDLC. There should occasionally be some...

49:31 - Nathaniel Elkins (nelknet.com)
  LDRL, DLC.

49:45 - T-Gro (Microsoft)
  It's also... Oh.

49:54 - Nathaniel Elkins (nelknet.com)
  Well, I mean, like, I mean, I... What is this? this? My IntelliSense isn't working because I don't have this set up properly, but RoomBall storage, closure info, I kind of see some of these descriptions here.

50:15 - T-Gro (Microsoft)
  Yeah, I'm looking at a huge declaration of all the different instructions. It's maybe in a different file. Let's see.  Or if you can search for, like, LDNow, for example, maybe this will be LDNow. Yeah, and just navigate to it, you can, where it's declared.

50:53 - Nathaniel Elkins (nelknet.com)
  Let me go to the top.

50:58 - T-Gro (Microsoft)
  It's declared in a different file, everybody. What's in this one? Yeah, it's in the abstract, the abstract IL. Yeah, this is the one.

51:05 - Nathaniel Elkins (nelknet.com)
  Okay, yeah. Yeah, is like this.

51:09 - T-Gro (Microsoft)
  Yeah, yeah, this is the one. So, this representation, like, might be the one to do the diffs of still, like, semantic understanding before it's converted to L right, and you could compare the old and new representation for the function bodies, and in this same file, you will also find the F# representation of the metadata above it, right, so functions, types, and before erasure, also closures and unions.

51:52 - Nathaniel Elkins (nelknet.com)
  In this file up here?

51:56 - T-Gro (Microsoft)
  Yeah, I think that this file has the declaration of both early instructions, but listening and替iced about it.

52:00 - Nathaniel Elkins (nelknet.com)
  three

52:00 - T-Gro (Microsoft)
  So, like, possibilities of types, yeah, how they exist in IL.

52:14 - Nathaniel Elkins (nelknet.com)
  Okay, well, I think I've got some reading to do, to just wrap my head around this, and you know how it is, you know, you stare at something for a while, and idea strikes, but that's good to know, and yeah, it seems like familiarizing myself with the different, you could say the different trees, or different levels of representation, both from the type tree, and then what's declared in this file, and then ILX, and then also the IL, and just seeing all the different representations, and where the right, what's the right level at which to represent the semantic edit, and then also going into C#, and seeing So, interesting.  You know, the flow of how they do things. Yeah.

53:07 - T-Gro (Microsoft)
  Yeah, the level affects a lot of things, because the more you are in the beginning, then you represent the intent of the user, but you can be very, very detached from the impact on the IL, because with every level going from source code via parsing and type checking, you are moving away from how the user wrote it, how the programmer wrote And hot reload is difficult in the aspect that it has to combine both worlds, right?  Because you are detecting deltas in what the user has written, but you still want to apply an IL representation.  Yeah. And that's where I guess it's difficult because the compiler has not been created to be adaptive in mind.  The compiler has been created to be a static tool. You feed all the static text and you create a binary, right?  Maybe some modern compilers could be built. Like, with incrementality in mind, but the FF# one isn't.

54:05 - Nathaniel Elkins (nelknet.com)
  Yeah, well, I think there are constructs, like, again, I think symbol matcher might be an important one to, you know, there's definitely some kind of construct within the Roslyn compiler for mapping the higher level definitions to the lower ones.  And I'm just going to have to look at this and kind of understand how they did it. And I think actually symbol matcher, I think actually another part of this, the symbol matcher thing, is matching symbols in the new compilation to symbols in the old one.  So I think that might be another piece. And maybe if we do this properly, then it'll make the whole line number thing, you know, it'll allow us to work around that.

55:13 - T-Gro (Microsoft)
  So we'll see. And one thing which I haven't understood. So the mechanism for hot reload, does it allow you to keep state in memory, or do you have to keep state on disk somewhere?  So when you want to save some pieces of information from the compilation, can you just have it in an in-memory object, or is it not stateful?

55:35 - Nathaniel Elkins (nelknet.com)
  It is stateful. It is stateful. So you can hold things in memory.

55:44 - T-Gro (Microsoft)
  Yeah. That makes things a lot easier, I believe, because that means that you don't have to invent a different serialization format to take the inner representation, be it the typed tree or the IlxGen output, and figure out how to persist it to disk and then read it from disk.

56:04 - Nathaniel Elkins (nelknet.com)
  Yeah, yeah, correct. And, I mean, well, when you say, like, stateful, I just want to make sure that we're talking about the same thing.  You mean, like, variables that are, like, I don't know, whatever is held in memory of the running application?

56:18 - T-Gro (Microsoft)
  Basically, if you are using Hot Reload and you are making one edit every five seconds, you still have, like, the same process which handles the Hot Reload, and it cannot just, like, keep things in objects to carry across the...  Yeah, exactly.

56:33 - Nathaniel Elkins (nelknet.com)
  Okay. Correct. And I think it's actually technically left totally up to the user of Hot Reload. Again, at the end of the day, the only thing that the runtime takes is an assembly object and then three bytes, or sorry, not three bytes, three arrays of bytes.  And so, however you want to keep, like, how... Where you're holding things in memory, in terms of holding on to the compilation context, that's left up to the implementer.  But I think that it should. And again, that would typically be something, some kind of like staple process that would be invoked via .NET startup hooks and that initialize method.  That would kind of run as a little program for the hot reload. Or like when you try and run something and you want to enable hot reload, it first invokes the .NET startup hooks, you know, invokes that initialization.  And then it's like a little program that runs and it kind of communicates via like named pipes or something like that.  That's how it works in Roslyn, where they have some like little program that kicks off and communicates maybe with like the .NET watch processor.  I don't like that via named pipes. So it's stable, I guess you could say.

58:06 - T-Gro (Microsoft)
  Yeah, okay. Now I understand it. Thanks. One thing I would also recommend maybe feeding into your context is the PDB writer.

58:19 - Nathaniel Elkins (nelknet.com)
  It's the ilwrite PDB writer.

58:22 - T-Gro (Microsoft)
  Because the PDB does also have a matching between the IL representation and what exists in the source code. Maybe it can be helpful.  I'm not sure how, but it does seem to be very related. And I assume that in order for debugging to work in Hot Reload, PDB files have to be taken into account as well.  They are basically a mapping between IL constructs and the syntax-level construct.

58:54 - Nathaniel Elkins (nelknet.com)
  Yeah. Okay. That is very useful. If we could take ilwrite—so if you look at here, like, what are, uh, but this, well, this maybe maps it to the source, but it doesn't map it to, like, the symbol, or does it?

59:24 - T-Gro (Microsoft)
  I think that PDB wraps it to, like, coordinates in the source file as text. you really have, okay, this method is on line 700, uh, column 10, so that, uh, debugging knows, like, where to show it to you.  Got it. And breakpoint is hit.

59:44 - Nathaniel Elkins (nelknet.com)
  Got it. Okay. All right, I will, I will keep that in mind and read over this as well. Um, so I.

1:00:02 - T-Gro (Microsoft)
  Maybe just like to close what I understood. So your next step after thinking about it will be like raising a decision document or starting a discussion somewhere public with your proposed approach?

1:00:18 - Nathaniel Elkins (nelknet.com)
  Yeah, yeah. think, well, I think, I mean, I don't necessarily know that I have a, let say, proposed approach.  Like, I don't have a super strong opinion, although I'm forming one. And as I get more and more into the details, I think the first thing I would like to do is document in depth how Roslyn does things.  Because, you know, they have, you know, what constructs they're using and what the different steps are along the way.  Because then just as you and I have been talking, we can find whether, hey, find whether, you this exists in some capacity in the F# pilot, which I may not know about yet.  Um, and then we can, we can see how. Existing constructs may be modified to support these new use cases.

1:01:08 - T-Gro (Microsoft)
  Yeah, that makes a lot of sense, because what Roslyn does is for sure a good basis, because I think all the steps which Roslyn has to do, F# has to do as well.  And F# has to do slightly more, because F# is semantically more detached from the IL. After all, we have a running joke that the CIL is a C# intermediate language, because the IL is very similar to C#, except for IL being stack-based and C# having the expressions object-oriented.  But when it comes to definitions of types and methods, it looks very similar when you compare IL and C#.  And F# does go a few more steps away from IL, which will make the F# hot reload more advanced compared to the C# one.

1:01:58 - Nathaniel Elkins (nelknet.com)
  yeah, yeah. I, I, I. Totally get that and have looked at my fair share of decompiled F# code. So that rings true.  Cool. All right. Well, thank you so much for taking the time to chat with me. Obviously, I'm working on this, but it's kind of like a free time thing.  So don't expect anything soon, but I'll try and make incremental progress. And of course, it's one of those things that once you get some people excited about it, and once you put your work in public, hopefully we'll be able to build a little momentum.  And yeah, you know, hey, five or six years is better late than never. Real question is, what are the chances we get this in for .NET 10?  Probably zero.

1:02:50 - T-Gro (Microsoft)
  But probably zero. But wow, that would be a great release. For showing all of this, this, I... ... I haven't studied it in depth, and also thanks for showing the tools how to work with that, especially the one for like printing the metadata tables will be a valuable debugging tool even for me, because so far I have only used like the inner representation in F#, and I didn't know I can like print it so nicely, like all the metadata tables and working with the offsets, it's really a nice to know thing.

1:03:30 - Nathaniel Elkins (nelknet.com)
  Yeah, to get the latest version, I added this little script, which looks at the, because again, this is not, or the version that's published on NuGet is super old, so I have been going to the, you know, the latest, like our latest CI builds to pull it, download it locally, and install the .NET tool, you know, from a local package.  Which I download. So that's one thing to be aware of.

1:04:03 - T-Gro (Microsoft)
  Yeah, I see.

1:04:07 - Nathaniel Elkins (nelknet.com)
  So, but again, hopefully, you I'm just going to like try and clean this up a little bit, but this is a separate repo.  And yeah, maybe I'll open up a PR, branch for the F# repo, and then, you know, we'll start with the documents and maybe some other, I don't know, related utilities.  Or I'll just, in a separate branch, we can always cut all that stuff later on or provide instructions, separate instructions on the best way to install these things.  Or again, even just have like a setup F# script like this that allows people to install tools easily. Unfortunately, it requires that a couple of different .NET versions are installed.  Like this Hot Reload Delta Gen, for example, requires .NET 10. So, you know. It's just some, like, stuff there, but, you people can figure that out, and again, I'll keep them in a separate branch, so it doesn't, in fact, anybody wants to work on that Fathom repo.

1:05:12 - T-Gro (Microsoft)
  If you're going to, like, grace any document or discussion about it, let me know. I can also ping a few people who are maybe not on Discord, like, regularly, but who still, like, know a thing or two about the inner representations and might share their opinion.  And I will be very glad to help and guide where needed, because I understand, like, this is a very huge, huge thing, and many people feared even starting it.  You are definitely the community hero.

1:05:50 - Nathaniel Elkins (nelknet.com)
  You know, it's, hey, look, many hands make light work. So, hopefully, as, again, as more information. It's unearthed. It'll, you know, it'll make it easier for folks to get involved.  And, you know, who knows? Maybe, like, you know, other people who are working on Fathom. We've got some really smart people in the F# community, you know?  There's that anonymous N-Cave guy who's done, like, a lot of work there. You know, we've got, obviously.

1:06:22 - T-Gro (Microsoft)
  People are saying it's a pseudonym of Don Sime. Like, many people, like, believe it's Don Sime under a different account.

1:06:32 - Nathaniel Elkins (nelknet.com)
  That's hilarious. I don't think so, but that would be very funny. Cool. All right. Well, again, thank you so much for taking the time.  And, yeah, I'll try and get on that doc. And I think just chatting in Discord, think connecting with people, you know, it's good.  And it's good to keep people motivated and throw ideas around. So we'll see where it takes us.

1:06:57 - T-Gro (Microsoft)
  Yeah, I'm definitely looking forward.

1:07:00 - Nathaniel Elkins (nelknet.com)
  Cool. All right. And of course, I didn't say it, but thanks for all the work that you do. I know, like, and I've heard from Will, and I've seen, like, you know, there's obviously, obviously there's a fair amount of tumult at Microsoft in general, but then I know, like, you know, a lot of, like, everyone kind of does their tour of duty in F#, but then moves on to something else, whatever.  I know it's like some tumult there, but I appreciate all the hard work that you've been doing with F#.  And, yeah, just know there's, you know, people enjoying the fruits of your labor every single day.

1:07:32 - T-Gro (Microsoft)
  Oh, I'm glad to hear that because that's why we do things, yeah, and why we are also pushing hard internally to keep Microsoft committed.  And I think we are making our case that the vibrant community, like, wouldn't survive the same hit like, for example, Basic did.  Like, Basic is now, like, fully abandoned on the kitten left and maintenance mode, and I don't think that the F# community deserve this treatment, and I think those are people who really like.  F# is the language and want to keep it vibrant and evolving.

1:08:05 - Nathaniel Elkins (nelknet.com)
  You know what? We just have to lean into the whole AI thing. We tried to shed that reputation for so long, but now that AI is the new hotness, we can lean into that.  I don't know if you ever remember that tweet by Jeremy Howard. He's like that big figure, Jeremy Howard. Let's see, have you, did you see?  I'm not sure. Let me see if I can find this. Yeah, Jeremy Howard, what is it? Here, this one.  This is an, it's an old, very old tweet, but...

1:08:50 - T-Gro (Microsoft)
  I'm not seeing it.

1:08:52 - Nathaniel Elkins (nelknet.com)
  Oh, one second. Let me find... Let me zoom go, and I'll send it in the chat. Zoom here. fun.  hear... 3 I think this guy, maybe he was behind like Fast.ai or something like that, but he's a big name in AI.  Yeah, Fast.ai, which was that deep learning course that was a big deal. He's a pretty big name, and you can see how many followers he has, whatever.  But just sell people on this tweet, and we can lean back into the AI stuff, math and AI. This is another reason why we need support for covariance or something like that, so that all the ML.net libraries and things like that can work easily.  Anyway, that's a whole separate thing.

1:09:50 - T-Gro (Microsoft)
  I have one more thought, which I just remembered and might be useful to you. I will send a link, and then I will finally thank you and say goodbye.  There is an... Auto-generated documentation of the F# compiler is this deep wiki link, but it's very cool. And if you are wipe coding a lot, this might like be a good starting point for the context because it's built by Devin, I believe, but it did a fairly decent job in showing and describing the various stages of the compiler as plain English as text by reverse engineering the code.  And maybe you will find a good way to simply feed this URL as context into your wiki link activities.

1:10:33 - Nathaniel Elkins (nelknet.com)
  Got it. Okay.

1:10:35 - T-Gro (Microsoft)
  Very cool. We were quite impressed by it. Even DonSign was like quite impressed by the results. And it's basically a compression scheme so that tools which try to work with code don't have to repeatedly build context from all the source files, but instead can use a compressed English version of the know-how about it.

1:10:54 - Nathaniel Elkins (nelknet.com)
  Yeah. Okay, cool. Cool. will take a look at this and I see it's even got this little like deep research.  this Cool. All right. All right. Thanks very much. And yeah, hopefully we'll talk soon. right.

1:11:09 - T-Gro (Microsoft)
  Have a good Thank you. And have fun with FF#.

1:11:11 - Nathaniel Elkins (nelknet.com)
  Yeah, thanks.
