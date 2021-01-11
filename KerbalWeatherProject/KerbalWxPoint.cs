﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace KerbalWeatherProject
{
    using WindDelegate = Func<CelestialBody, Part, Vector3, Vector3>;
    using PropertyDelegate = Func<CelestialBody, Vector3d, double, double>;
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class KerbalWxPoint : MonoBehaviour
    {

        internal const string MODID = "KerbalWeatherProject_NS";
        internal const string MODNAME = "KerbalWeatherProject";

        // wind
        public static Vector3 windVectorWS; // final wind direction and magnitude in world space
        Vector3 windVector; // wind in "map" space, y = north, x = east (?)
        //Setting booleans
        bool use_point;
        bool aero;
        bool thermo;
        public bool wx_enabled = true;
        public bool gotMPAS = false;
        public bool haveFAR = false;
        public bool disable_surface_wind = false;

        //Define wind bools
        public bool cnst_wnd;
        public int wspd_prof;
        public int wdir_prof;

        const int nvars = 8; //Variable dimension (number of 3D, full-atmosphere variables)
        const int nsvars = 6; //Surface Variable dimension (number of 2D surface variables)

        //Define velocity and weather lists
        List<double> vel_list = new List<double>();
        List<double> wx_list2d = new List<double>();
        List<double> wx_list3d = new List<double>();

        //Set celestial body
        CelestialBody kerbin = Util.getbody();
        
        // book keeping data
        Matrix4x4 worldframe = Matrix4x4.identity; // orientation of the planet surface under the vessel in world space

        //Check if FAR is available
        bool CheckFAR()
        {
            try
            {
                //Define type methods
                Type OldWindFunction = null;
                Type FARAtm = null;
                Type PresFunction = typeof(PropertyDelegate);
                Type TempFunction = typeof(PropertyDelegate);
                Type WindFunction = typeof(WindDelegate);

                //Search assembly
                foreach (var assembly in AssemblyLoader.loadedAssemblies)
                {
                    if (assembly.name == "FerramAerospaceResearch")
                    {
                        //Get Far assembly info
                        var types = assembly.assembly.GetExportedTypes();

                        foreach (Type t in types)
                        {
                            if (t.FullName.Equals("FerramAerospaceResearch.FARWind"))
                            {
                                FARAtm = t;
                            }
                            if (t.FullName.Equals("FerramAerospaceResearch.FARAtmosphere"))
                            {
                                FARAtm = t;
                            }
                            if (t.FullName.Equals("FerramAerospaceResarch.FARWind+WindFunction"))
                            {
                                OldWindFunction = t;
                            }
                        }
                    }
                }

                //If no wind or atmosphere cs available return false
                if (FARAtm == null)
                {
                    return false;
                }

                //Check if Old Version of FAR is installed
                if (OldWindFunction != null)
                {
                    //Get FAR Wind Method
                    MethodInfo SetWindFunction = FARAtm.GetMethod("SetWindFunction");
                    if (SetWindFunction == null)
                    {
                        return false;
                    }
                    //Set FARWind function
                    var del = Delegate.CreateDelegate(OldWindFunction, this, typeof(KerbalWxPoint).GetMethod("GetTheWindPoint"), true);
                    SetWindFunction.Invoke(null, new object[] { del });
                }
                else
                {

                    //Get FAR Atmosphere Methods 
                    MethodInfo SetWindFunction = FARAtm.GetMethod("SetWindFunction");
                    MethodInfo SetTempFunction = FARAtm.GetMethod("SetTemperatureFunction");
                    MethodInfo SetPresFunction = FARAtm.GetMethod("SetPressureFunction");

                    //If no wind function available return false
                    if (SetWindFunction == null)
                    {
                        return false;
                    }

                    // Set FAR Atmosphere functions

                    var del1 = Delegate.CreateDelegate(WindFunction, this, typeof(KerbalWxPoint).GetMethod("GetTheWindPoint"), true); // typeof(KerbalWxPoint).GetMethod("GetTheWindPoint"), true);                                                                                                                                      //Util.Log("del1: " + del1);
                    SetWindFunction.Invoke(null, new object[] { del1 });

                    var del2 = Delegate.CreateDelegate(TempFunction, this, typeof(KerbalWxPoint).GetMethod("GetTheTemperaturePoint"), true); // typeof(KerbalWxPoint).GetMethod("GetTheWindPoint"), true);
                    SetTempFunction.Invoke(null, new object[] { del2 });

                    var del3 = Delegate.CreateDelegate(PresFunction, this, typeof(KerbalWxPoint).GetMethod("GetThePressurePoint"), true); // typeof(KerbalWxPoint).GetMethod("GetTheWindPoint"), true);
                    SetPresFunction.Invoke(null, new object[] { del3 });
                    //Util.Log("SetPressureFunction: " + SetPresFunction + ", del3: " + SetPresFunction);

                }
                return true; // jump out
            }
            catch (Exception e)
            {
                Debug.LogError("KerbalWeatherProject: unable to register with FerramAerospaceResearch. Exception thrown: " + e.ToString());
            }
            return false;
        }

        //Check game settings
        void check_settings()
        {
            //Check to see if weather or climate data is to be used.
            use_point = Util.useWX();

            //Check to see if aero or thermo effects have been turned off
            aero = Util.allowAero();
            thermo = Util.allowThermo();

            //Determine if KWP is enabled
            wx_enabled = Util.getWindBool();

            //Get surface wind boolean
            disable_surface_wind = HighLogic.CurrentGame.Parameters.CustomParams<KerbalWxCustomParams_Sec2>().disable_surface_wind;

            //Retrieve wind profile booleans and parameters
            cnst_wnd = HighLogic.CurrentGame.Parameters.CustomParams<KerbalWxCustomParams_Sec2>().use_cnstprofile;
            wspd_prof = HighLogic.CurrentGame.Parameters.CustomParams<KerbalWxCustomParams_Sec2>().set_wspeed;
            wdir_prof = HighLogic.CurrentGame.Parameters.CustomParams<KerbalWxCustomParams_Sec2>().set_wdir;

        }

        void Awake()
        {
            //Initialize wind vector
            windVectorWS = Vector3.zero;

            //Check settings
            check_settings();

            //Register with FAR
            haveFAR = CheckFAR();

            //Initialize vel data
            for (int i = 0; i < nvars; i++)
            {
                vel_list.Add(0);
            }

            //Initialize Wx list 2D
            for (int i = 0; i < nsvars + 2; i++)
            {
                wx_list2d.Add(0);
            }

            //Initialize Wx list 3D
            for (int i = 0; i < nvars+1; i++)
            {
                wx_list3d.Add(0);
            }
        }

        //Define world refence frame
        bool UpdateCoords()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return false;
            Vector3 east = vessel.east;
            Vector3 north = vessel.north;
            Vector3 up = vessel.upAxis;

            worldframe[0, 2] = east.x;
            worldframe[1, 2] = east.y;
            worldframe[2, 2] = east.z;
            worldframe[0, 1] = up.x;
            worldframe[1, 1] = up.y;
            worldframe[2, 1] = up.z;
            worldframe[0, 0] = north.x;
            worldframe[1, 0] = north.y;
            worldframe[2, 0] = north.z;
            return true;
        }

        void FixedUpdate()
        {

            check_settings();
            //If were not using point MPAS data or were not in flight return void
            if ((!HighLogic.LoadedSceneIsFlight) || (!use_point))
            {
                return;
            }
            Vessel vessel = FlightGlobals.ActiveVessel;
            //Get vehicle position
            double vheight = vessel.altitude;
            if ((wx_enabled) && (use_point))
            {
                UpdateCoords();

                if ((FlightGlobals.ActiveVessel.mainBody != kerbin))
                {
                    return;
                }

                string lsite0 = Util.get_last_lsite();
                //Check to see if launch site has changed
                string lsite = vessel.launchedFrom;

                //Rename launchpad to KSC
                if (lsite != lsite0)
                {
                    gotMPAS = false;
                }

                //Get launch site from list
                int lidx;
                if (weather_api.lsites_name.Contains(lsite))
                {
                    lidx = weather_api.lsites_name.IndexOf(lsite);
                }
                else
                {
                    //If launch site is not in list find the nearest launch site from current position
                    double mlat = vessel.latitude;
                    double mlng = vessel.longitude;
                    lidx = weather_api.get_nearest_lsite_idx(mlat, mlng);
                }

                //Save launch site
                Util.save_lsite(lsite);
                Util.save_lsite_short(weather_api.lsites[lidx]);

                //If launch site has changed retrieved weather data for new launch site
                if (!gotMPAS)
                {
                    if (weather_api.lsites_name.Contains(lsite)) { 
                        //If launch site is known (i.e., in KSP or Kerbinside remastered)
                        lidx = weather_api.lsites_name.IndexOf(lsite);
                        //Retrieve weather data for launch site
                        weather_data.get_wxdata(Util.get_last_lsite_short());
                        gotMPAS = true; //set data read boolean
                    } else { 

                        //Find nearest launch site given current position
                        double mlat = vessel.latitude;
                        double mlng = vessel.longitude;
                        int midx = weather_api.get_nearest_lsite_idx(mlat, mlng);
                        //Retrieve weather data for nearest launch site
                        weather_data.get_wxdata(Util.get_last_lsite_short());
                        gotMPAS = true; //set data read boolean
                    }
                }

                if ((FlightGlobals.ActiveVessel.mainBody == kerbin) && (vessel.altitude >= 70000))
                {
                    //Get 2D meteorological fields
                    double olr; double precipw; double mslp; double sst; double tcld; double rain; double tsfc; double rhsfc; double uwnd_sfc; double vwnd_sfc;
                    weather_data.wx_srf weather_data2D = weather_data.getAmbientWx2D(); //Retrieve ambient surface weather data
                    //Get individual fields from struct
                    olr = weather_data2D.olr; tcld = weather_data2D.cloudcover; mslp = weather_data2D.mslp; precipw = weather_data2D.precitable_water; rain = weather_data2D.precipitation_rate;
                    sst = weather_data2D.sst; tsfc = weather_data2D.temperature; rhsfc = weather_data2D.humidity; uwnd_sfc = weather_data2D.wind_x; vwnd_sfc = weather_data2D.wind_y;
                    //Compute wind speed
                    double wspd_sfc = Math.Sqrt(Math.Pow(uwnd_sfc, 2) + Math.Pow(vwnd_sfc, 2));

                    //Ensure total cloud cover is not negative.
                    if (tcld < 0.0)
                    {
                        tcld = 0.0;
                    }

                    //Save 2D surface meteorological fields to list
                    wx_list2d[0] = olr;
                    wx_list2d[1] = tcld;
                    wx_list2d[2] = precipw;
                    wx_list2d[3] = rain;
                    wx_list2d[4] = mslp;
                    wx_list2d[5] = tsfc;
                    wx_list2d[6] = rhsfc;
                    wx_list2d[7] = wspd_sfc;
                    return;
                }
                if (FlightGlobals.ready && FlightGlobals.ActiveVessel != null)
                {
                    double u; double v; double w; double t; double p; double rh; double vis; double cldfrac; double d;
                    //Retrieve 3D meteorological fields
                    weather_data.wx_atm weather_data3D = weather_data.getAmbientWx3D(vheight); //Get 3-D weather data
                    //Extract individual fields from struct
                    u = weather_data3D.wind_x; v = weather_data3D.wind_y; w = weather_data3D.wind_z; t = weather_data3D.temperature; d = weather_data3D.density;
                    p = weather_data3D.pressure; rh = weather_data3D.humidity; vis = weather_data3D.visibility; cldfrac = weather_data3D.cloudcover;
                    //Set wind to zero if landed or if wind near surface is dsiabled
                    if (((vessel.LandedOrSplashed) || (vessel.heightFromTerrain < 50)) && (disable_surface_wind))
                    {
                        u = 0; v = 0; w = 0;
                    }

                    //Override MPAS wind if a wind profile is selected/enabled.
                    if (cnst_wnd)
                    {
                        //Compute wind components
                        u = -wspd_prof * Math.Sin((Math.PI / 180.0) * wdir_prof);
                        v = -wspd_prof * Math.Cos((Math.PI / 180.0) * wdir_prof);
                        w = 0; //no vertical motion
                    }

                    //Retrieve 2D meteorological fields
                    double olr; double precipw; double mslp; double sst; double tcld; double rain; double tsfc; double rhsfc; double uwnd_sfc; double vwnd_sfc;
                    weather_data.wx_srf weather_data2D = weather_data.getAmbientWx2D(); //Get surface weather data
                    //Extract individual fields
                    olr = weather_data2D.olr; tcld = weather_data2D.cloudcover; mslp = weather_data2D.mslp; precipw = weather_data2D.precitable_water; rain = weather_data2D.precipitation_rate;
                    sst = weather_data2D.sst; tsfc = weather_data2D.temperature; rhsfc = weather_data2D.humidity; uwnd_sfc = weather_data2D.wind_x; vwnd_sfc = weather_data2D.wind_y;
                    windVector.x = (float)v; //North
                    windVector.y = (float)w; //Up
                    windVector.z = (float)u; //East
                    windVectorWS = worldframe * windVector;
                    //Compute horizontal wind speed
                    double wspd_sfc = Math.Sqrt(Math.Pow(uwnd_sfc, 2) + Math.Pow(vwnd_sfc, 2));
                    //Ensure total cloud cover is non-negative
                    if (tcld < 0)
                    {
                        tcld = 0;
                    }

                    //3D atmospheric fields
                    wx_list3d[0] = v;
                    wx_list3d[1] = u;
                    wx_list3d[2] = w;
                    wx_list3d[3] = p;
                    wx_list3d[4] = t;
                    wx_list3d[5] = rh;
                    wx_list3d[6] = vis;
                    wx_list3d[7] = cldfrac;
                    wx_list3d[8] = d;

                    //2D atmospheric fields
                    wx_list2d[0] = olr;
                    wx_list2d[1] = tcld;
                    wx_list2d[2] = precipw;
                    wx_list2d[3] = rain;
                    wx_list2d[4] = mslp;
                    wx_list2d[5] = tsfc;
                    wx_list2d[6] = rhsfc;
                    wx_list2d[7] = wspd_sfc;

                    //Get vehicle/wind relative velocity 
                    double vel_ias; double vel_tas; double vel_eas; double vel_grnd; double vel_par; double vel_prp;
                    weather_data.wx_vel vdata;
                    if (cnst_wnd)
                    {
                       vdata = weather_data.getVehicleVelCnst(vessel, u, v, w, wx_enabled);
                    }
                    else
                    {
                        vdata = weather_data.getVehicleVel(vessel, vheight, wx_enabled);
                    }

                    vel_ias = vdata.vel_ias; vel_tas = vdata.vel_tas; vel_eas = vdata.vel_eas; vel_grnd = vdata.vel_grnd; vel_par = vdata.vel_par; vel_prp = vdata.vel_prp;
                    vel_list[0] = vel_ias; //Indicated airspeed
                    vel_list[1] = vel_tas; //True airspeed (accounts for FAR Wind)
                    vel_list[2] = vel_eas; //Equivalent Airspeed 
                    vel_list[3] = vel_grnd; //Surface velocity (i.e. ground speed)
                    vel_list[4] = vel_par; //Component of wind perpendicular to aircraft
                    vel_list[5] = vel_prp; //Component of wind parallel to aircraft

                    Vector3d vwrld = vessel.GetWorldPos3D();
                    if (aero == false)
                    {
                        //3D atmospheric fields
                        wx_list3d[0] = 0;
                        wx_list3d[1] = 0;
                        wx_list3d[2] = 0;
                    }
                    if (thermo == false)
                    {
                        //3D atmospheric fields
                        wx_list3d[3] = Util.GetPressure(vwrld) * 1000;
                        wx_list3d[4] = Util.GetTemperature(vwrld);
                        wx_list3d[5] = 0;
                        wx_list3d[6] = 0;
                        wx_list3d[7] = 0;
                        wx_list3d[8] = Util.GetDensity(vwrld);
                    }
                }
                else
                {
                    windVectorWS = Vector3.zero;
                }
            }
            else
            {
                //Get vehicle/wind relative velocity 
                double vel_ias; double vel_tas; double vel_eas; double vel_grnd; double vel_par; double vel_prp;
                weather_data.wx_vel vdata;
                if (cnst_wnd)
                {
                    vdata = weather_data.getVehicleVelCnst(vessel, 0, 0, 0, wx_enabled);
                }
                else
                {
                    vdata = weather_data.getVehicleVel(vessel, vheight, wx_enabled);
                }

                vel_ias = vdata.vel_ias; vel_tas = vdata.vel_tas; vel_eas = vdata.vel_eas; vel_grnd = vdata.vel_grnd; vel_par = vdata.vel_par; vel_prp = vdata.vel_prp;
                vel_list[0] = vel_ias; //Indicated airspeed
                vel_list[1] = vel_tas; //True airspeed (accounts for FAR Wind)
                vel_list[2] = vel_eas; //Equivalent Airspeed 
                vel_list[3] = vel_grnd; //Surface velocity (i.e. ground speed)
                vel_list[4] = vel_par; //Component of wind perpendicular to aircraft
                vel_list[5] = vel_prp; //Component of wind parallel to aircraft

                Vector3d vwrld = vessel.GetWorldPos3D();
                //3D atmospheric fields
                wx_list3d[0] = 0;
                wx_list3d[1] = 0;
                wx_list3d[2] = 0;
                wx_list3d[3] = Util.GetPressure(vwrld)*1000;
                wx_list3d[4] = Util.GetTemperature(vwrld);
                wx_list3d[5] = 0;
                wx_list3d[6] = 0;
                wx_list3d[7] = 0;
                wx_list3d[8] = Util.GetDensity(vwrld);

                //2D atmospheric fields
                wx_list2d[0] = 0;
                wx_list2d[1] = 0;
                wx_list2d[2] = 0;
                wx_list2d[3] = 0;
                wx_list2d[4] = 0;
                wx_list2d[5] = 0;
                wx_list2d[6] = 0;
                wx_list2d[7] = 0;

                windVectorWS = Vector3.zero;
            }
        }
        public List<double> getAero()
        {
            return vel_list;
        }
        public Vector3d get3DWind()
        {
            return windVector;
        }

        public List<double> getWx3D()
        {
            return wx_list3d;
        }
        public static Vector3 getWSWind()
        {
            return windVectorWS;
        }

        public List<double> getWx2D()
        {
            return wx_list2d;
        }

        //Called by FAR. Returns wind vector.
        public Vector3 GetTheWindPoint(CelestialBody body, Part part, Vector3 position)
        {
            if (!part || (part.partBuoyancy && part.partBuoyancy.splashed))
            {
                return Vector3.zero;
            }
            else
            {
                return windVectorWS;
            }
        }

        //Called by FAR. Returns wind vector.
        public double GetTheTemperaturePoint(CelestialBody body, Vector3d latlonAltitude, double ut)
        {
            //Retrieve air temperature at vessel location
            return wx_list3d[4];
        }

        //Called by FAR. Returns ambient pressure.
        public double GetThePressurePoint(CelestialBody body, Vector3d latlonAltitude, double ut)
        {
            //Retrieve air pressure at vessel location
            return wx_list3d[3];
        }

    }
}